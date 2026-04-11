using System.Runtime.InteropServices;

namespace RendererDotNet.Fonts;

/// <summary>
/// Minimal P/Invoke bindings for FreeType 2.x — only the functions needed
/// for glyph rendering (matching the subset used by tr_font.c).
/// </summary>
internal static unsafe class FreeType
{
    private const string Lib = "freetype";

    // Opaque handle types
    public readonly struct FT_Library { public readonly nint Handle; public bool IsNull => Handle == 0; }
    public readonly struct FT_Face { public readonly nint Handle; public bool IsNull => Handle == 0; }

    // Error code (0 = success)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FT_Init_FreeType(out FT_Library library);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FT_Done_FreeType(FT_Library library);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FT_New_Memory_Face(FT_Library library, byte* fileBase, int fileSize, int faceIndex, out FT_Face face);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FT_Done_Face(FT_Face face);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FT_Set_Char_Size(FT_Face face, int charWidth, int charHeight, uint horzResolution, uint vertResolution);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FT_Load_Glyph(FT_Face face, uint glyphIndex, int loadFlags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FT_Get_Char_Index(FT_Face face, uint charCode);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FT_Outline_Translate(FT_Outline* outline, int xOffset, int yOffset);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FT_Outline_Get_Bitmap(FT_Library library, FT_Outline* outline, FT_Bitmap* bitmap);

    // FT_LOAD flags
    public const int FT_LOAD_DEFAULT = 0;

    // FT_Glyph_Format
    public const int FT_GLYPH_FORMAT_OUTLINE = 0x6F75746C; // 'outl'

    // FT_Pixel_Mode
    public const byte FT_PIXEL_MODE_MONO = 1;
    public const byte FT_PIXEL_MODE_GRAY = 2;

    // 26.6 fixed-point helpers (matching C macros _FLOOR, _CEIL, _TRUNC)
    public static int Floor26_6(int x) => x & -64;
    public static int Ceil26_6(int x) => (x + 63) & -64;
    public static int Trunc26_6(int x) => x >> 6;

    // ---- FreeType struct layouts (Windows x64, FreeType 2.14.x) ----
    // These are the raw structs we read from the FT_Face / FT_GlyphSlot pointers.
    // We use explicit offsets to access only the fields we need.

    /// <summary>Read the glyph slot pointer from an FT_FaceRec.</summary>
    public static FT_GlyphSlotRec* GetGlyphSlot(FT_Face face)
    {
        // FT_FaceRec.glyph is at a known offset.
        // On x64 (all pointers 8 bytes): the field order in FT_FaceRec is:
        //   num_faces(long=8), face_index(long=8), face_flags(long=8), style_flags(long=8),
        //   num_glyphs(long=8), family_name(ptr=8), style_name(ptr=8), num_fixed_sizes(int=4),
        //   available_sizes(ptr=8), num_charmaps(int=4), charmaps(ptr=8), generic(16),
        //   bbox(4*long=32), units_per_EM(ushort=2), ascender(short=2), descender(short=2),
        //   height(short=2), max_advance_width(short=2), max_advance_height(short=2),
        //   underline_position(short=2), underline_thickness(short=2),
        //   glyph(ptr=8), ...
        // Rather than computing exact offset, we use the FreeType ABI helper below.
        return *(FT_GlyphSlotRec**)((byte*)face.Handle + GlyphSlotOffset);
    }

    // Offset of the 'glyph' pointer within FT_FaceRec on Windows x64.
    // Computed once at init time by measuring the struct layout.
    // FT_FaceRec fields before glyph (on 64-bit with FT_Long = long = 8 bytes):
    //   num_faces: 8, face_index: 8, face_flags: 8, style_flags: 8,
    //   num_glyphs: 8, family_name: 8, style_name: 8,
    //   num_fixed_sizes: 4 + padding: 4, available_sizes: 8,
    //   num_charmaps: 4 + padding: 4, charmaps: 8,
    //   generic: {data:8, finalizer:8} = 16,
    //   bbox: {xMin:8, yMin:8, xMax:8, yMax:8} = 32,
    //   units_per_EM: 2, ascender: 2, descender: 2, height: 2,
    //   max_advance_width: 2, max_advance_height: 2,
    //   underline_position: 2, underline_thickness: 2,
    //   glyph: 8
    // Total offset = 5*8 + 2*8 + (4+4) + 8 + (4+4) + 8 + 16 + 32 + 8*2 + 8
    //             = 40 + 16 + 8 + 8 + 8 + 8 + 16 + 32 + 16 + 8 = 160
    // But FT_Long on MSVC Windows is actually `long` which is 4 bytes, not 8!
    // MSVC `long` = 4 bytes. FT_Long = long = 4 bytes on Windows.
    // Re-computing with FT_Long=4:
    //   num_faces: 4, face_index: 4, face_flags: 4, style_flags: 4,
    //   num_glyphs: 4, + padding to 8-align family_name: 4,
    //   family_name: 8, style_name: 8,
    //   num_fixed_sizes: 4, + padding: 4, available_sizes: 8,
    //   num_charmaps: 4, + padding: 4, charmaps: 8,
    //   generic: {data:8, finalizer:8} = 16,
    //   bbox: {xMin:4, yMin:4, xMax:4, yMax:4} = 16 (FT_BBox uses FT_Pos = FT_Long = 4),
    //   units_per_EM: 2, ascender: 2, descender: 2, height: 2,
    //   max_advance_width: 2, max_advance_height: 2,
    //   underline_position: 2, underline_thickness: 2,
    //   glyph: 8
    // Let me be more precise. Actually FT_Pos = long on MSVC = 4 bytes.
    // But wait: on 64-bit MSVC, `long` is 4 bytes! This is the famous Windows quirk.
    // FT_Long = signed long, FT_ULong = unsigned long, FT_Pos = signed long.
    // All 4 bytes on Win64.
    //
    // Layout with proper alignment:
    // Offset 0:  FT_Long    num_faces        (4)
    // Offset 4:  FT_Long    face_index       (4)  
    // Offset 8:  FT_Long    face_flags       (4)
    // Offset 12: FT_Long    style_flags      (4)
    // Offset 16: FT_Long    num_glyphs       (4)
    // Offset 20: padding                     (4) -- align to 8 for pointer
    // Offset 24: FT_String* family_name      (8)
    // Offset 32: FT_String* style_name       (8)
    // Offset 40: FT_Int     num_fixed_sizes  (4)
    // Offset 44: padding                     (4)
    // Offset 48: FT_Bitmap_Size* available_sizes (8)
    // Offset 56: FT_Int     num_charmaps     (4)
    // Offset 60: padding                     (4)
    // Offset 64: FT_CharMap* charmaps        (8)
    // Offset 72: FT_Generic generic          (16) = {data:8, finalizer:8}
    // Offset 88: FT_BBox    bbox             (16) = {xMin:4, yMin:4, xMax:4, yMax:4}
    // Offset 104: FT_UShort  units_per_EM    (2)
    // Offset 106: FT_Short   ascender        (2)
    // Offset 108: FT_Short   descender       (2)
    // Offset 110: FT_Short   height          (2)
    // Offset 112: FT_Short   max_advance_width  (2)
    // Offset 114: FT_Short   max_advance_height (2)
    // Offset 116: FT_Short   underline_position (2)
    // Offset 118: FT_Short   underline_thickness (2)
    // Offset 120: FT_GlyphSlot glyph         (8)
    internal const int GlyphSlotOffset = 120;

    /// <summary>Glyph slot record — we only read the fields we need via offsets.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FT_GlyphSlotRec
    {
        // We access fields via explicit byte offsets from the struct start.
        // FT_GlyphSlotRec layout (MSVC x64, FT_Long=4):
        //   library: 8, face: 8, next: 8, glyph_index: 4, generic: 16,
        //   metrics: FT_Glyph_Metrics (8 * FT_Pos=4 = 32),
        //   linearHoriAdvance: 4, linearVertAdvance: 4,
        //   advance: FT_Vector (2*4=8),
        //   format: 4 (FT_Glyph_Format = int),
        //   bitmap: FT_Bitmap (see below),
        //   bitmap_left: 4, bitmap_top: 4,
        //   outline: FT_Outline (see below),
        //   ...
    }

    // Offsets within FT_GlyphSlotRec (MSVC x64)
    //   Offset 0:  FT_Library    library   (8)
    //   Offset 8:  FT_Face       face      (8)
    //   Offset 16: FT_GlyphSlot  next      (8)
    //   Offset 24: FT_UInt       glyph_index (4)
    //   Offset 28: padding       (4) -- align to 8
    //   Offset 32: FT_Generic    generic   (16)
    //   Offset 48: FT_Glyph_Metrics metrics (8 * FT_Pos = 32)
    //     metrics.width    at +48
    //     metrics.height   at +52
    //     metrics.horiBearingX at +56
    //     metrics.horiBearingY at +60
    //     metrics.horiAdvance at +64
    //     metrics.vertBearingX at +68
    //     metrics.vertBearingY at +72
    //     metrics.vertAdvance at +76
    //   Offset 80: FT_Fixed linearHoriAdvance (4, FT_Fixed = FT_Long = 4)
    //   Offset 84: FT_Fixed linearVertAdvance (4)
    //   Offset 88: FT_Vector advance (8) = {x:4, y:4}
    //   Offset 96: FT_Glyph_Format format (4, it's an enum/int)
    //   Offset 100: padding (4) -- FT_Bitmap requires 8-byte alignment (contains pointers)
    //   Offset 104: FT_Bitmap bitmap (40 bytes) -- verified with offsetof()
    //   Offset 144: FT_Int bitmap_left (4)
    //   Offset 148: FT_Int bitmap_top (4)
    //   Offset 152: FT_Outline outline (40 bytes) -- verified with offsetof()

    // We access these as raw byte offsets from the glyph slot pointer.
    // Verified via compiled C offsetof() program against MSVC x64 FreeType 2.14.
    public const int SlotMetricsOffset = 48;       // FT_Glyph_Metrics starts here
    public const int SlotFormatOffset = 96;         // FT_Glyph_Format
    public const int SlotOutlineOffset = 152;       // FT_Outline

    /// <summary>Read glyph metrics from a glyph slot.</summary>
    public static void GetGlyphMetrics(FT_GlyphSlotRec* slot,
        out int width, out int height,
        out int horiBearingX, out int horiBearingY,
        out int horiAdvance)
    {
        int* m = (int*)((byte*)slot + SlotMetricsOffset);
        width = m[0];
        height = m[1];
        horiBearingX = m[2];
        horiBearingY = m[3];
        horiAdvance = m[4];
    }

    /// <summary>Read glyph format from a glyph slot.</summary>
    public static int GetGlyphFormat(FT_GlyphSlotRec* slot)
    {
        return *(int*)((byte*)slot + SlotFormatOffset);
    }

    /// <summary>Get pointer to the outline within a glyph slot.</summary>
    public static FT_Outline* GetOutline(FT_GlyphSlotRec* slot)
    {
        return (FT_Outline*)((byte*)slot + SlotOutlineOffset);
    }

    /// <summary>FT_Outline — variable-length struct, we only pass pointers.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FT_Outline
    {
        public short n_contours;
        public short n_points;
        // followed by pointers and flags — we don't need to read these
    }

    /// <summary>FT_Bitmap used for rendering glyphs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FT_Bitmap
    {
        public uint rows;
        public uint width;
        public int pitch;
        private int _padding;
        public byte* buffer;
        public ushort num_grays;
        public byte pixel_mode;
        public byte palette_mode;
        private int _padding2;
        public nint palette;
    }
}
