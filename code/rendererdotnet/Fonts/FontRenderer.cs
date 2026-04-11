using RendererDotNet.Interop;

namespace RendererDotNet.Fonts;

/// <summary>
/// Renders TrueType fonts using FreeType, producing glyph atlas textures.
/// Port of the C implementation in tr_font.c.
/// </summary>
public static unsafe class FontRenderer
{
    private static FreeType.FT_Library _ftLib;
    private static bool _initialized;

    private const int GLYPH_START = 0;
    private const int GLYPH_END = 255;
    private const int ATLAS_SIZE = 256;

    /// <summary>Initialize FreeType library. Call once at renderer startup.</summary>
    public static bool Init()
    {
        if (_initialized) return true;

        try
        {
            int err = FreeType.FT_Init_FreeType(out _ftLib);
            if (err != 0 || _ftLib.IsNull)
            {
                EngineImports.Printf(EngineImports.PRINT_WARNING,
                    "[.NET] FreeType initialization failed\n");
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                "[.NET] freetype.dll not found — TrueType font rendering disabled\n");
            return false;
        }

        _initialized = true;
        EngineImports.Printf(EngineImports.PRINT_ALL,
            "[.NET] FreeType initialized\n");
        return true;
    }

    /// <summary>Shutdown FreeType library. Call at renderer shutdown.</summary>
    public static void Shutdown()
    {
        if (_initialized && !_ftLib.IsNull)
        {
            FreeType.FT_Done_FreeType(_ftLib);
            _ftLib = default;
        }
        _initialized = false;
    }

    /// <summary>
    /// Render a TrueType font at the given point size, producing fontInfo_t data.
    /// Returns true on success. On failure, the fontInfo buffer is not modified.
    /// </summary>
    public static bool RenderFont(string fontName, int pointSize, byte* fontInfoPtr,
        ShaderManager shaders, Renderer2D renderer)
    {
        if (!_initialized || shaders == null || renderer == null)
            return false;

        // Read font file from engine filesystem
        int len = EngineImports.FS_ReadFile(fontName, out byte* faceData);
        if (len <= 0 || faceData == null)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] FreeType: Unable to read font file '{fontName}'\n");
            return false;
        }

        FreeType.FT_Face face;
        int err = FreeType.FT_New_Memory_Face(_ftLib, faceData, len, 0, out face);
        if (err != 0 || face.IsNull)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                "[.NET] FreeType: Unable to create face\n");
            EngineImports.FS_FreeFile(faceData);
            return false;
        }

        const uint dpi = 72;
        err = FreeType.FT_Set_Char_Size(face, pointSize << 6, pointSize << 6, dpi, dpi);
        if (err != 0)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                "[.NET] FreeType: Unable to set char size\n");
            FreeType.FT_Done_Face(face);
            EngineImports.FS_FreeFile(faceData);
            return false;
        }

        // Allocate 256x256 grayscale atlas buffer
        byte[] atlasBuffer = new byte[ATLAS_SIZE * ATLAS_SIZE];

        // First pass: calculate max glyph height
        int maxHeight = 0;
        for (int i = GLYPH_START; i <= GLYPH_END; i++)
        {
            GetGlyphInfo(face, (byte)i, out _, out _, out int h, out _, out _);
            if (h > maxHeight) maxHeight = h;
        }

        // Second pass: render glyphs into atlas pages
        int xOut = 0, yOut = 0;
        int lastStart = GLYPH_START;
        int imageNumber = 0;

        // Temporary storage for per-glyph info
        var glyphs = new GlyphData[256];
        int i2 = GLYPH_START;

        while (i2 <= GLYPH_END + 1)
        {
            bool pageComplete = false;

            if (i2 == GLYPH_END + 1)
            {
                // Final page — upload whatever remains
                pageComplete = true;
            }
            else
            {
                // Render glyph and place in atlas
                var glyph = RenderGlyph(face, (byte)i2, atlasBuffer, ref xOut, ref yOut, maxHeight);
                if (xOut == -1 || yOut == -1)
                {
                    pageComplete = true;
                }
                else
                {
                    glyphs[i2] = glyph;
                    i2++;
                }
            }

            if (pageComplete)
            {
                // Convert grayscale atlas to RGBA (white text + alpha)
                byte[] rgba = new byte[ATLAS_SIZE * ATLAS_SIZE * 4];
                float max = 0;
                for (int k = 0; k < ATLAS_SIZE * ATLAS_SIZE; k++)
                {
                    if (atlasBuffer[k] > max) max = atlasBuffer[k];
                }
                float scale = max > 0 ? 255.0f / max : 0;

                for (int k = 0; k < ATLAS_SIZE * ATLAS_SIZE; k++)
                {
                    int off = k * 4;
                    rgba[off] = 255;     // R
                    rgba[off + 1] = 255; // G
                    rgba[off + 2] = 255; // B
                    rgba[off + 3] = (byte)(atlasBuffer[k] * scale); // A
                }

                // Upload texture
                string shaderName = $"fonts/fontImage_{imageNumber}_{pointSize}.tga";
                uint texId;
                fixed (byte* rgbaPtr = rgba)
                {
                    texId = renderer.CreateTexture(ATLAS_SIZE, ATLAS_SIZE, rgbaPtr,
                        clamp: true, generateMipmaps: false);
                }

                // Register shader and assign to all glyphs in this page
                int handle = shaders.RegisterWithTextureId(shaderName, texId);

                for (int j = lastStart; j < i2; j++)
                {
                    glyphs[j].ShaderHandle = handle;
                    glyphs[j].ShaderName = shaderName;
                }

                lastStart = i2;
                imageNumber++;

                // Clear atlas for next page
                Array.Clear(atlasBuffer);
                xOut = 0;
                yOut = 0;

                if (i2 == GLYPH_END + 1) i2++;
            }
        }

        // Compute glyph scale (relative to 48pt at 72dpi, matching C code)
        float glyphScale = 72.0f / dpi * (48.0f / pointSize);

        // Write fontInfo_t to the output buffer
        WriteFontInfo(fontInfoPtr, glyphs, glyphScale, pointSize);

        FreeType.FT_Done_Face(face);
        EngineImports.FS_FreeFile(faceData);

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] FreeType: Rendered '{fontName}' at {pointSize}pt ({imageNumber} atlas page(s))\n");

        return true;
    }

    /// <summary>Get glyph dimensions without rendering (for maxHeight calculation).</summary>
    private static void GetGlyphInfo(FreeType.FT_Face face, byte c,
        out int width, out int pitch, out int height, out int top, out int bottom)
    {
        uint glyphIndex = FreeType.FT_Get_Char_Index(face, c);
        FreeType.FT_Load_Glyph(face, glyphIndex, FreeType.FT_LOAD_DEFAULT);

        var slot = FreeType.GetGlyphSlot(face);
        FreeType.GetGlyphMetrics(slot,
            out int mWidth, out int mHeight,
            out int horiBearingX, out int horiBearingY,
            out _);

        int left = FreeType.Floor26_6(horiBearingX);
        int right = FreeType.Ceil26_6(horiBearingX + mWidth);
        width = FreeType.Trunc26_6(right - left);

        int topVal = FreeType.Ceil26_6(horiBearingY);
        int bottomVal = FreeType.Floor26_6(horiBearingY - mHeight);
        height = FreeType.Trunc26_6(topVal - bottomVal);

        // Pitch: aligned to 4 bytes (matching C code with qtrue path)
        pitch = (width + 3) & -4;
        top = (horiBearingY >> 6) + 1;
        bottom = bottomVal;
    }

    /// <summary>Render a single glyph into the atlas buffer.</summary>
    private static GlyphData RenderGlyph(FreeType.FT_Face face, byte c,
        byte[] atlas, ref int xOut, ref int yOut, int maxHeight)
    {
        var glyph = new GlyphData();

        uint glyphIndex = FreeType.FT_Get_Char_Index(face, c);
        FreeType.FT_Load_Glyph(face, glyphIndex, FreeType.FT_LOAD_DEFAULT);

        var slot = FreeType.GetGlyphSlot(face);
        int format = FreeType.GetGlyphFormat(slot);

        if (format != FreeType.FT_GLYPH_FORMAT_OUTLINE)
            return glyph;

        FreeType.GetGlyphMetrics(slot,
            out int mWidth, out int mHeight,
            out int horiBearingX, out int horiBearingY,
            out int horiAdvance);

        int left = FreeType.Floor26_6(horiBearingX);
        int right = FreeType.Ceil26_6(horiBearingX + mWidth);
        int width = FreeType.Trunc26_6(right - left);

        int topVal = FreeType.Ceil26_6(horiBearingY);
        int bottomVal = FreeType.Floor26_6(horiBearingY - mHeight);
        int height = FreeType.Trunc26_6(topVal - bottomVal);
        int pitch = (width + 3) & -4;

        glyph.Height = height;
        glyph.Pitch = pitch;
        glyph.Top = (horiBearingY >> 6) + 1;
        glyph.Bottom = bottomVal;
        glyph.XSkip = FreeType.Trunc26_6(horiAdvance) + 1;
        glyph.ImageWidth = width;
        glyph.ImageHeight = height;

        if (width == 0 || height == 0)
        {
            glyph.S = glyph.T = glyph.S2 = glyph.T2 = 0;
            return glyph;
        }

        // Check if glyph fits in current row
        if (xOut + width + 1 >= 255)
        {
            xOut = 0;
            yOut += maxHeight + 1;
        }

        // Check if we've run out of vertical space — need new page
        if (yOut + maxHeight + 1 >= 255)
        {
            xOut = -1;
            yOut = -1;
            return glyph;
        }

        // Render glyph outline to a temporary bitmap
        var bitmap = new FreeType.FT_Bitmap();
        bitmap.width = (uint)width;
        bitmap.rows = (uint)height;
        bitmap.pitch = pitch;
        bitmap.pixel_mode = FreeType.FT_PIXEL_MODE_GRAY;
        bitmap.num_grays = 256;

        byte[] bitmapBuf = new byte[pitch * height];
        fixed (byte* bufPtr = bitmapBuf)
        {
            bitmap.buffer = bufPtr;

            var outline = FreeType.GetOutline(slot);
            FreeType.FT_Outline_Translate(outline, -left, -bottomVal);
            FreeType.FT_Outline_Get_Bitmap(_ftLib, outline, &bitmap);

            // Copy rendered glyph into atlas
            for (int row = 0; row < height; row++)
            {
                int srcOff = row * pitch;
                int dstOff = (yOut + row) * ATLAS_SIZE + xOut;
                for (int col = 0; col < width; col++)
                {
                    if (dstOff + col < atlas.Length)
                        atlas[dstOff + col] = bitmapBuf[srcOff + col];
                }
            }
        }

        // Set UV coordinates
        glyph.S = (float)xOut / ATLAS_SIZE;
        glyph.T = (float)yOut / ATLAS_SIZE;
        glyph.S2 = glyph.S + (float)width / ATLAS_SIZE;
        glyph.T2 = glyph.T + (float)height / ATLAS_SIZE;

        xOut += width + 1;

        return glyph;
    }

    /// <summary>Write the completed fontInfo_t structure to the engine's buffer.</summary>
    private static void WriteFontInfo(byte* dst, GlyphData[] glyphs, float glyphScale, int pointSize)
    {
        // fontInfo_t layout:
        //   glyphInfo_t glyphs[256]  — 256 × 80 = 20480 bytes
        //   float glyphScale         — 4 bytes (offset 20480)
        //   char name[64]            — 64 bytes (offset 20484)
        // Total: 20548 bytes

        const int GLYPH_SIZE = 80;

        for (int i = 0; i < 256; i++)
        {
            int off = i * GLYPH_SIZE;
            var g = glyphs[i];

            *(int*)(dst + off + 0) = g.Height;
            *(int*)(dst + off + 4) = g.Top;
            *(int*)(dst + off + 8) = g.Bottom;
            *(int*)(dst + off + 12) = g.Pitch;
            *(int*)(dst + off + 16) = g.XSkip;
            *(int*)(dst + off + 20) = g.ImageWidth;
            *(int*)(dst + off + 24) = g.ImageHeight;
            *(float*)(dst + off + 28) = g.S;
            *(float*)(dst + off + 32) = g.T;
            *(float*)(dst + off + 36) = g.S2;
            *(float*)(dst + off + 40) = g.T2;
            *(int*)(dst + off + 44) = g.ShaderHandle;

            // Write shader name (32 bytes, null-terminated)
            string name = g.ShaderName ?? "";
            for (int j = 0; j < 32; j++)
            {
                dst[off + 48 + j] = j < name.Length ? (byte)name[j] : (byte)0;
            }
        }

        // glyphScale at offset 20480
        *(float*)(dst + 20480) = glyphScale;

        // name at offset 20484 (64 bytes)
        string fontName = $"fonts/fontImage_{pointSize}.dat";
        for (int j = 0; j < 64; j++)
        {
            dst[20484 + j] = j < fontName.Length ? (byte)fontName[j] : (byte)0;
        }
    }

    private struct GlyphData
    {
        public int Height, Top, Bottom, Pitch, XSkip, ImageWidth, ImageHeight;
        public float S, T, S2, T2;
        public int ShaderHandle;
        public string? ShaderName;
    }
}
