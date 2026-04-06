using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RendererDotNet.Interop;

namespace RendererDotNet;

/// <summary>
/// Parses Q3 .shader script files to resolve shader names to image paths.
/// Mirrors the approach in renderergl2/tr_shader.c ScanAndLoadShaderFiles().
///
/// Q3 shader scripts define multi-stage materials. Each shader has a name
/// and one or more stages containing "map" directives that reference texture files.
/// This parser extracts the first usable image path from each shader definition.
/// </summary>
public sealed unsafe class ShaderScriptParser
{
    private readonly Dictionary<string, ShaderDef> _shaders = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _shaders.Count;

    /// <summary>
    /// Load and parse all .shader files from the engine's scripts/ directory.
    /// Uses FS_ListFiles("scripts", ".shader") just like the C renderer does.
    /// </summary>
    public void LoadAllShaders()
    {
        byte** fileList = EngineImports.FS_ListFiles("scripts", ".shader", out int numFiles);
        if (fileList == null || numFiles == 0)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                "[.NET] WARNING: no shader files found\n");
            return;
        }

        int loaded = 0;
        for (int i = 0; i < numFiles; i++)
        {
            string fileName = Marshal.PtrToStringUTF8((nint)fileList[i]) ?? "";
            if (string.IsNullOrEmpty(fileName))
                continue;

            string path = $"scripts/{fileName}";
            int len = EngineImports.FS_ReadFile(path, out byte* buf);
            if (len <= 0 || buf == null)
            {
                if (buf != null) EngineImports.FS_FreeFile(buf);
                continue;
            }

            string text = Encoding.UTF8.GetString(buf, (int)len);
            EngineImports.FS_FreeFile(buf);

            ParseShaderFile(text, path);
            loaded++;
        }

        EngineImports.FS_FreeFileList(fileList);
        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Parsed {loaded} shader files, {_shaders.Count} shader definitions\n");
    }

    /// <summary>
    /// Look up the primary image path for a shader name.
    /// Returns null if the shader isn't defined in any script file.
    /// </summary>
    public ShaderDef? GetShaderDef(string name)
    {
        _shaders.TryGetValue(name, out var def);
        return def;
    }

    /// <summary>
    /// Find the first shader with a skyparms outerbox definition.
    /// Returns the sky box texture base name, or null if none found.
    /// </summary>
    public string? FindSkyBoxName()
    {
        foreach (var def in _shaders.Values)
        {
            if (def.SkyBox != null)
                return def.SkyBox;
        }
        return null;
    }

    /// <summary>
    /// Parse all shader definitions from a single .shader file's text content.
    /// Format:
    ///   shaderName
    ///   {
    ///       // global directives (cull, surfaceparm, etc.)
    ///       {
    ///           map texturePath.tga
    ///           blendFunc ...
    ///       }
    ///   }
    /// </summary>
    private void ParseShaderFile(string text, string filename)
    {
        var tokenizer = new ShaderTokenizer(text);

        while (tokenizer.HasMore())
        {
            string? shaderName = tokenizer.NextToken();
            if (shaderName == null)
                break;

            // Expect opening brace for shader body
            string? openBrace = tokenizer.NextToken();
            if (openBrace != "{")
            {
                EngineImports.Printf(EngineImports.PRINT_WARNING,
                    $"[.NET] WARNING: expected '{{' after shader '{shaderName}' in {filename}\n");
                break;
            }

            var def = ParseSingleShader(shaderName, ref tokenizer);
            if (def != null && !_shaders.ContainsKey(shaderName))
            {
                _shaders[shaderName] = def;
            }
        }
    }

    /// <summary>
    /// Parse a single shader definition (everything between the outer { }).
    /// Extracts the first usable "map" image path and blendFunc from the shader's stages.
    /// </summary>
    private ShaderDef? ParseSingleShader(string name, ref ShaderTokenizer tokenizer)
    {
        int depth = 1;
        string? imagePath = null;
        bool clamp = false;
        BlendMode blend = BlendMode.Opaque;
        bool foundUsableStage = false;
        string? skyBox = null;
        string? editorImage = null;
        bool isTransparent = false;
        string? envMapImage = null; // fallback: first env-mapped stage's image
        BlendMode envMapBlend = BlendMode.Opaque;
        bool allStagesEnvMap = true; // track if ALL stages use tcGen environment
        int stageCount = 0;
        int alphaFunc = 0; // 0=none, 1=GT0, 2=LT128, 3=GE128
        int cullMode = 0;  // 0=front (default Q3), 1=back, 2=none/twosided
        bool hasEnvMap = false; // any stage uses tcGen environment
        string[]? animFrames = null;
        float animFrequency = 0;

        // Per-stage tracking
        string? stageImage = null;
        bool stageClamp = false;
        BlendMode stageBlend = BlendMode.Opaque;
        bool stageHasEnvMap = false;
        int stageAlphaFunc = 0;
        string[]? stageAnimFrames = null;
        float stageAnimFrequency = 0;
        var stageTcMods = new List<TcMod>();
        TcMod[]? tcMods = null;
        int stageRgbGen = 0;
        int stageAlphaGen = 0;
        int stageDepthFunc = 0;
        bool stageDepthWrite = false;
        int rgbGen = 0;
        int alphaGen = 0;
        bool polygonOffset = false;
        bool entityMergable = false;
        int depthFunc = 0; // 0=lequal, 1=equal
        bool depthWrite = false;
        int sortKey = 0;
        var deforms = new List<DeformVertexes>();
        bool hasFogParms = false;
        float fogColorR = 0, fogColorG = 0, fogColorB = 0;
        float fogDepthForOpaque = 0;
        bool noLightMap = false;
        bool noDLight = false;
        bool noMarks = false;
        bool noPicMip = false;
        bool noMipMaps = false;
        bool isPortal = false;
        string? normalMapPath = null;
        string? specularMapPath = null;
        bool stageIsLightmap = false;
        bool stageIsWhiteImage = false;
        string? stageMapRaw = null; // raw map token including $lightmap/$whiteimage
        int stageVideoMapHandle = -1; // cinematic handle from videoMap directive
        var allStages = new List<ShaderStage>();

        while (depth > 0 && tokenizer.HasMore())
        {
            string? token = tokenizer.NextToken();
            if (token == null)
                break;

            if (token == "{")
            {
                depth++;
                if (depth == 2)
                {
                    stageImage = null;
                    stageClamp = false;
                    stageBlend = BlendMode.Opaque;
                    stageHasEnvMap = false;
                    stageAlphaFunc = 0;
                    stageAnimFrames = null;
                    stageAnimFrequency = 0;
                    stageTcMods.Clear();
                    stageRgbGen = 0;
                    stageAlphaGen = 0;
                    stageDepthFunc = 0;
                    stageDepthWrite = false;
                    stageIsLightmap = false;
                    stageIsWhiteImage = false;
                    stageMapRaw = null;
                    stageVideoMapHandle = -1;
                }
                continue;
            }

            if (token == "}")
            {
                if (depth == 2)
                {
                    stageCount++;
                    // When leaving a stage, adopt the first stage that has a real image
                    // and doesn't use environment mapping
                    if (!foundUsableStage && stageImage != null && !stageHasEnvMap)
                    {
                        imagePath = stageImage;
                        clamp = stageClamp;
                        blend = stageBlend;
                        alphaFunc = stageAlphaFunc;
                        foundUsableStage = true;
                    }
                    // Track env-mapped images as a last-resort fallback
                    if (stageHasEnvMap && stageImage != null && envMapImage == null)
                    {
                        envMapImage = stageImage;
                        envMapBlend = stageBlend;
                    }
                    // Track if any stage is NOT envmap
                    if (!stageHasEnvMap)
                        allStagesEnvMap = false;
                    // Capture alphaFunc from any stage (first one wins)
                    if (alphaFunc == 0 && stageAlphaFunc != 0)
                        alphaFunc = stageAlphaFunc;
                    // Capture animMap frames from first animated stage
                    if (animFrames == null && stageAnimFrames != null)
                    {
                        animFrames = stageAnimFrames;
                        animFrequency = stageAnimFrequency;
                    }
                    // Capture tcMod from first usable stage
                    if (tcMods == null && stageTcMods.Count > 0)
                        tcMods = stageTcMods.ToArray();
                    // Capture rgbGen/alphaGen from first usable stage
                    if (rgbGen == 0 && stageRgbGen != 0)
                        rgbGen = stageRgbGen;
                    if (alphaGen == 0 && stageAlphaGen != 0)
                        alphaGen = stageAlphaGen;
                    // Capture depthFunc/depthWrite from first stage
                    if (depthFunc == 0 && stageDepthFunc != 0)
                        depthFunc = stageDepthFunc;
                    if (!depthWrite && stageDepthWrite)
                        depthWrite = true;

                    // Collect full stage data for multi-pass rendering
                    if (stageImage != null || stageIsLightmap || stageIsWhiteImage ||
                        stageAnimFrames != null || stageVideoMapHandle >= 0)
                    {
                        allStages.Add(new ShaderStage
                        {
                            ImagePath = stageImage,
                            IsLightmap = stageIsLightmap,
                            IsWhiteImage = stageIsWhiteImage,
                            Clamp = stageClamp,
                            Blend = stageBlend,
                            AlphaFunc = stageAlphaFunc,
                            HasEnvMap = stageHasEnvMap,
                            AnimFrames = stageAnimFrames,
                            AnimFrequency = stageAnimFrequency,
                            TcMods = stageTcMods.Count > 0 ? stageTcMods.ToArray() : null,
                            RgbGen = stageRgbGen,
                            AlphaGen = stageAlphaGen,
                            DepthFunc = stageDepthFunc,
                            DepthWrite = stageDepthWrite,
                            VideoMapHandle = stageVideoMapHandle,
                        });
                    }
                }

                depth--;
                continue;
            }

            // Parse global shader directives (depth == 1)
            if (depth == 1)
            {
                if (string.Equals(token, "skyparms", StringComparison.OrdinalIgnoreCase))
                {
                    string? outerBox = tokenizer.NextToken();
                    tokenizer.NextToken(); // cloudheight
                    tokenizer.NextToken(); // innerbox
                    if (outerBox != null && outerBox != "-")
                        skyBox = outerBox;
                }
                else if (string.Equals(token, "qer_editorimage", StringComparison.OrdinalIgnoreCase))
                {
                    string? edImg = tokenizer.NextToken();
                    if (edImg != null && !IsSpecialMap(edImg))
                        editorImage = edImg;
                }
                else if (string.Equals(token, "surfaceparm", StringComparison.OrdinalIgnoreCase))
                {
                    string? parm = tokenizer.NextToken();
                    if (parm != null)
                    {
                        if (string.Equals(parm, "trans", StringComparison.OrdinalIgnoreCase))
                            isTransparent = true;
                        else if (string.Equals(parm, "nolightmap", StringComparison.OrdinalIgnoreCase))
                            noLightMap = true;
                        else if (string.Equals(parm, "nodlight", StringComparison.OrdinalIgnoreCase))
                            noDLight = true;
                        else if (string.Equals(parm, "nomarks", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(parm, "noimpact", StringComparison.OrdinalIgnoreCase))
                            noMarks = true;
                    }
                }
                else if (string.Equals(token, "nopicmip", StringComparison.OrdinalIgnoreCase))
                {
                    noPicMip = true;
                }
                else if (string.Equals(token, "nomipmaps", StringComparison.OrdinalIgnoreCase))
                {
                    noMipMaps = true;
                    noPicMip = true; // nomipmaps implies nopicmip (Q3 behavior)
                }
                else if (string.Equals(token, "cull", StringComparison.OrdinalIgnoreCase))
                {
                    string? cullToken = tokenizer.NextToken();
                    if (cullToken != null)
                    {
                        if (string.Equals(cullToken, "none", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(cullToken, "twosided", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(cullToken, "disable", StringComparison.OrdinalIgnoreCase))
                            cullMode = 2;
                        else if (string.Equals(cullToken, "back", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(cullToken, "backside", StringComparison.OrdinalIgnoreCase))
                            cullMode = 1;
                    }
                }
                else if (string.Equals(token, "polygonOffset", StringComparison.OrdinalIgnoreCase))
                {
                    polygonOffset = true;
                }
                else if (string.Equals(token, "entityMergable", StringComparison.OrdinalIgnoreCase))
                {
                    entityMergable = true;
                }
                else if (string.Equals(token, "fogParms", StringComparison.OrdinalIgnoreCase))
                {
                    // fogParms ( <r> <g> <b> ) <depthForOpaque>
                    // or fogParms <r> <g> <b> <depthForOpaque>
                    string? next = tokenizer.NextToken();
                    if (next == "(") next = tokenizer.NextToken(); // skip open paren
                    if (next != null && float.TryParse(next, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fr)) fogColorR = fr;
                    next = tokenizer.NextToken();
                    if (next != null && float.TryParse(next, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fg)) fogColorG = fg;
                    next = tokenizer.NextToken();
                    if (next != null && float.TryParse(next, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fb)) fogColorB = fb;
                    next = tokenizer.NextToken();
                    if (next == ")") next = tokenizer.NextToken(); // skip close paren
                    if (next != null && float.TryParse(next, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fd)) fogDepthForOpaque = fd;
                    hasFogParms = true;
                }
                else if (string.Equals(token, "deformVertexes", StringComparison.OrdinalIgnoreCase))
                {
                    var deform = ParseDeformVertexes(ref tokenizer);
                    if (deform.HasValue)
                        deforms.Add(deform.Value);
                }
                else if (string.Equals(token, "sort", StringComparison.OrdinalIgnoreCase))
                {
                    string? sortToken = tokenizer.NextToken();
                    if (sortToken != null)
                    {
                        if (string.Equals(sortToken, "portal", StringComparison.OrdinalIgnoreCase)) { sortKey = 1; isPortal = true; }
                        else if (string.Equals(sortToken, "sky", StringComparison.OrdinalIgnoreCase)) sortKey = 2;
                        else if (string.Equals(sortToken, "opaque", StringComparison.OrdinalIgnoreCase)) sortKey = 3;
                        else if (string.Equals(sortToken, "decal", StringComparison.OrdinalIgnoreCase)) sortKey = 4;
                        else if (string.Equals(sortToken, "seeThrough", StringComparison.OrdinalIgnoreCase)) sortKey = 5;
                        else if (string.Equals(sortToken, "banner", StringComparison.OrdinalIgnoreCase)) sortKey = 6;
                        else if (string.Equals(sortToken, "additive", StringComparison.OrdinalIgnoreCase)) sortKey = 9;
                        else if (string.Equals(sortToken, "nearest", StringComparison.OrdinalIgnoreCase)) sortKey = 16;
                        else if (int.TryParse(sortToken, out int numSort)) sortKey = numSort;
                    }
                }
            }

            // Parse directives inside stages (depth == 2)
            if (depth == 2)
            {
                if (string.Equals(token, "map", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null)
                    {
                        stageMapRaw = mapToken;
                        if (string.Equals(mapToken, "$lightmap", StringComparison.OrdinalIgnoreCase))
                            stageIsLightmap = true;
                        else if (string.Equals(mapToken, "$whiteimage", StringComparison.OrdinalIgnoreCase))
                            stageIsWhiteImage = true;
                        else if (!IsSpecialMap(mapToken) && stageImage == null)
                            stageImage = mapToken;
                    }
                }
                else if (string.Equals(token, "clampMap", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null && !IsSpecialMap(mapToken) && stageImage == null)
                    {
                        stageImage = mapToken;
                        stageClamp = true;
                    }
                }
                else if (string.Equals(token, "videoMap", StringComparison.OrdinalIgnoreCase))
                {
                    string? videoFile = tokenizer.NextToken();
                    if (videoFile != null)
                    {
                        // CIN_loop=2, CIN_silent=8, CIN_shader=16
                        int cinHandle = EngineImports.CIN_PlayCinematic(videoFile, 0, 0, 256, 256, 2 | 8 | 16);
                        if (cinHandle >= 0)
                            stageVideoMapHandle = cinHandle;
                    }
                }
                else if (string.Equals(token, "animMap", StringComparison.OrdinalIgnoreCase))
                {
                    string? freqToken = tokenizer.NextToken();
                    float animFreq = 0;
                    if (freqToken != null)
                        float.TryParse(freqToken, System.Globalization.CultureInfo.InvariantCulture, out animFreq);

                    // Collect all frame paths until end of line (next token that starts a new directive)
                    var frames = new List<string>();
                    while (true)
                    {
                        string? frameToken = tokenizer.NextToken();
                        if (frameToken == null || frameToken == "{" || frameToken == "}")
                        {
                            if (frameToken != null)
                                tokenizer.PushBack(frameToken);
                            break;
                        }
                        // If token looks like a directive keyword, we've gone too far
                        if (IsStageDirective(frameToken))
                        {
                            tokenizer.PushBack(frameToken);
                            break;
                        }
                        frames.Add(frameToken);
                    }

                    if (frames.Count > 0 && stageImage == null)
                        stageImage = frames[0];

                    if (frames.Count > 1 && stageAnimFrames == null)
                    {
                        stageAnimFrames = frames.ToArray();
                        stageAnimFrequency = animFreq;
                    }
                }
                else if (string.Equals(token, "normalMap", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(token, "bumpMap", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null && !IsSpecialMap(mapToken))
                        normalMapPath = mapToken;
                }
                else if (string.Equals(token, "specularMap", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null && !IsSpecialMap(mapToken))
                        specularMapPath = mapToken;
                }
                else if (string.Equals(token, "blendFunc", StringComparison.OrdinalIgnoreCase))
                {
                    stageBlend = ParseBlendFunc(ref tokenizer);
                }
                else if (string.Equals(token, "tcGen", StringComparison.OrdinalIgnoreCase))
                {
                    string? tcGenToken = tokenizer.NextToken();
                    if (tcGenToken != null && string.Equals(tcGenToken, "environment", StringComparison.OrdinalIgnoreCase))
                    {
                        stageHasEnvMap = true;
                        hasEnvMap = true;
                    }
                }
                else if (string.Equals(token, "alphaFunc", StringComparison.OrdinalIgnoreCase))
                {
                    string? funcToken = tokenizer.NextToken();
                    if (funcToken != null)
                    {
                        if (string.Equals(funcToken, "GT0", StringComparison.OrdinalIgnoreCase))
                            stageAlphaFunc = 1;
                        else if (string.Equals(funcToken, "LT128", StringComparison.OrdinalIgnoreCase))
                            stageAlphaFunc = 2;
                        else if (string.Equals(funcToken, "GE128", StringComparison.OrdinalIgnoreCase))
                            stageAlphaFunc = 3;
                    }
                }
                else if (string.Equals(token, "tcMod", StringComparison.OrdinalIgnoreCase))
                {
                    var tcMod = ParseTcMod(ref tokenizer);
                    if (tcMod.HasValue)
                        stageTcMods.Add(tcMod.Value);
                }
                else if (string.Equals(token, "rgbGen", StringComparison.OrdinalIgnoreCase))
                {
                    string? rgbToken = tokenizer.NextToken();
                    if (rgbToken != null)
                    {
                        if (string.Equals(rgbToken, "vertex", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(rgbToken, "exactVertex", StringComparison.OrdinalIgnoreCase))
                            stageRgbGen = 1;
                        else if (string.Equals(rgbToken, "entity", StringComparison.OrdinalIgnoreCase))
                            stageRgbGen = 2;
                        else if (string.Equals(rgbToken, "wave", StringComparison.OrdinalIgnoreCase))
                        {
                            stageRgbGen = 3;
                            // Consume wave params: func base amp phase freq
                            tokenizer.NextToken(); tokenizer.NextToken();
                            tokenizer.NextToken(); tokenizer.NextToken();
                            tokenizer.NextToken();
                        }
                        else if (string.Equals(rgbToken, "identityLighting", StringComparison.OrdinalIgnoreCase))
                            stageRgbGen = 4;
                        else if (string.Equals(rgbToken, "const", StringComparison.OrdinalIgnoreCase))
                        {
                            // Consume const params: ( r g b )
                            tokenizer.NextToken(); tokenizer.NextToken();
                            tokenizer.NextToken(); tokenizer.NextToken();
                            tokenizer.NextToken();
                        }
                    }
                }
                else if (string.Equals(token, "alphaGen", StringComparison.OrdinalIgnoreCase))
                {
                    string? alphaToken = tokenizer.NextToken();
                    if (alphaToken != null)
                    {
                        if (string.Equals(alphaToken, "vertex", StringComparison.OrdinalIgnoreCase))
                            stageAlphaGen = 1;
                        else if (string.Equals(alphaToken, "entity", StringComparison.OrdinalIgnoreCase))
                            stageAlphaGen = 2;
                        else if (string.Equals(alphaToken, "wave", StringComparison.OrdinalIgnoreCase))
                        {
                            stageAlphaGen = 3;
                            // Consume wave params: func base amp phase freq
                            tokenizer.NextToken(); tokenizer.NextToken();
                            tokenizer.NextToken(); tokenizer.NextToken();
                            tokenizer.NextToken();
                        }
                        else if (string.Equals(alphaToken, "const", StringComparison.OrdinalIgnoreCase))
                        {
                            // Consume const value
                            tokenizer.NextToken();
                        }
                    }
                }
                else if (string.Equals(token, "depthFunc", StringComparison.OrdinalIgnoreCase))
                {
                    string? dfToken = tokenizer.NextToken();
                    if (dfToken != null && string.Equals(dfToken, "equal", StringComparison.OrdinalIgnoreCase))
                        stageDepthFunc = 1;
                }
                else if (string.Equals(token, "depthWrite", StringComparison.OrdinalIgnoreCase))
                {
                    stageDepthWrite = true;
                }
            }
        }

        // Fallback chain: qer_editorimage → env-mapped stage image → shader name itself
        if (imagePath == null)
        {
            imagePath = editorImage ?? envMapImage;
            // Use the env stage's blend mode when falling back to its image
            if (imagePath == envMapImage && envMapImage != null)
                blend = envMapBlend;
        }
        // If all stages were env-mapped with no map directive (implicit $whiteimage),
        // try the shader name itself as a texture path
        if (imagePath == null && stageCount > 0 && allStagesEnvMap)
            imagePath = name;

        // For multi-stage shaders, the shader-level blend determines opaque vs transparent
        // sorting. Use stage 0's blend mode: if stage 0 is opaque (lightmap, no blendfunc),
        // the shader is opaque regardless of later stages' compositing blend modes.
        // This prevents lightmap×texture shaders (GL_DST_COLOR GL_ZERO in stage 1)
        // from being incorrectly classified as transparent.
        if (allStages.Count > 1)
        {
            blend = allStages[0].Blend;
        }

        return new ShaderDef
        {
            Name = name,
            ImagePath = imagePath,
            Clamp = clamp,
            Blend = blend,
            SkyBox = skyBox,
            IsTransparent = isTransparent,
            AlphaFunc = alphaFunc,
            CullMode = cullMode,
            HasEnvMap = hasEnvMap,
            AnimFrames = animFrames,
            AnimFrequency = animFrequency,
            TcMods = tcMods,
            RgbGen = rgbGen,
            AlphaGen = alphaGen,
            PolygonOffset = polygonOffset,
            EntityMergable = entityMergable,
            DepthFunc = depthFunc,
            DepthWrite = depthWrite,
            SortKey = sortKey,
            Deforms = deforms.Count > 0 ? deforms.ToArray() : null,
            Stages = allStages.Count > 0 ? allStages.ToArray() : null,
            HasFogParms = hasFogParms,
            FogColorR = fogColorR,
            FogColorG = fogColorG,
            FogColorB = fogColorB,
            FogDepthForOpaque = fogDepthForOpaque,
            NoLightMap = noLightMap,
            NoDLight = noDLight,
            NoMarks = noMarks,
            NoPicMip = noPicMip,
            NoMipMaps = noMipMaps,
            IsPortal = isPortal,
            NormalMapPath = normalMapPath,
            SpecularMapPath = specularMapPath
        };
    }

    /// <summary>
    /// Parse a blendFunc directive: either shorthand (add/filter/blend)
    /// or long form (GL_ONE GL_ONE, etc.)
    /// </summary>
    private static BlendMode ParseBlendFunc(ref ShaderTokenizer tokenizer)
    {
        string? src = tokenizer.NextToken();
        if (src == null || src == "{" || src == "}") return BlendMode.Alpha;

        // Shorthand forms
        if (string.Equals(src, "add", StringComparison.OrdinalIgnoreCase))
            return BlendMode.Add;
        if (string.Equals(src, "filter", StringComparison.OrdinalIgnoreCase))
            return BlendMode.Filter;
        if (string.Equals(src, "blend", StringComparison.OrdinalIgnoreCase))
            return BlendMode.Alpha;

        // Long form: blendFunc <srcFactor> <dstFactor>
        string? dst = tokenizer.NextToken();
        if (dst == null || dst == "{" || dst == "}") return BlendMode.Alpha;

        return new BlendMode
        {
            SrcFactor = ParseGLFactor(src),
            DstFactor = ParseGLFactor(dst)
        };
    }

    private static int ParseGLFactor(string factor)
    {
        if (factor.Equals("GL_ZERO", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ZERO;
        if (factor.Equals("GL_ONE", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE;
        if (factor.Equals("GL_SRC_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_SRC_COLOR;
        if (factor.Equals("GL_ONE_MINUS_SRC_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_SRC_COLOR;
        if (factor.Equals("GL_SRC_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_SRC_ALPHA;
        if (factor.Equals("GL_ONE_MINUS_SRC_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_SRC_ALPHA;
        if (factor.Equals("GL_DST_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_DST_ALPHA;
        if (factor.Equals("GL_ONE_MINUS_DST_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_DST_ALPHA;
        if (factor.Equals("GL_DST_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_DST_COLOR;
        if (factor.Equals("GL_ONE_MINUS_DST_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_DST_COLOR;
        if (factor.Equals("GL_SRC_ALPHA_SATURATE", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_SRC_ALPHA_SATURATE;
        return BlendMode.GL_ONE; // Default
    }

    private static bool IsSpecialMap(string token)
    {
        return token.StartsWith('$') ||
               string.Equals(token, "$lightmap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "$whiteimage", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "$deluxemap", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStageDirective(string token)
    {
        return string.Equals(token, "map", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "clampMap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "animMap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "blendFunc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "alphaFunc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "tcGen", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "tcMod", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "rgbGen", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "alphaGen", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "depthFunc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "depthWrite", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "normalMap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "bumpMap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "specularMap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "videoMap", StringComparison.OrdinalIgnoreCase);
    }

    private static TcMod? ParseTcMod(ref ShaderTokenizer tokenizer)
    {
        string? type = tokenizer.NextToken();
        if (type == null) return null;

        var ci = System.Globalization.CultureInfo.InvariantCulture;

        if (string.Equals(type, "scroll", StringComparison.OrdinalIgnoreCase))
        {
            string? s = tokenizer.NextToken();
            string? t = tokenizer.NextToken();
            float sSpeed = 0, tSpeed = 0;
            if (s != null) float.TryParse(s, ci, out sSpeed);
            if (t != null) float.TryParse(t, ci, out tSpeed);
            return new TcMod { Type = TcModType.Scroll, Param0 = sSpeed, Param1 = tSpeed };
        }
        else if (string.Equals(type, "scale", StringComparison.OrdinalIgnoreCase))
        {
            string? s = tokenizer.NextToken();
            string? t = tokenizer.NextToken();
            float sScale = 1, tScale = 1;
            if (s != null) float.TryParse(s, ci, out sScale);
            if (t != null) float.TryParse(t, ci, out tScale);
            return new TcMod { Type = TcModType.Scale, Param0 = sScale, Param1 = tScale };
        }
        else if (string.Equals(type, "rotate", StringComparison.OrdinalIgnoreCase))
        {
            string? d = tokenizer.NextToken();
            float deg = 0;
            if (d != null) float.TryParse(d, ci, out deg);
            return new TcMod { Type = TcModType.Rotate, Param0 = deg };
        }
        else if (string.Equals(type, "turb", StringComparison.OrdinalIgnoreCase))
        {
            string? b = tokenizer.NextToken();
            string? a = tokenizer.NextToken();
            string? p = tokenizer.NextToken();
            string? f = tokenizer.NextToken();
            float ba = 0, am = 0, ph = 0, fr = 0;
            if (b != null) float.TryParse(b, ci, out ba);
            if (a != null) float.TryParse(a, ci, out am);
            if (p != null) float.TryParse(p, ci, out ph);
            if (f != null) float.TryParse(f, ci, out fr);
            return new TcMod { Type = TcModType.Turb, Param0 = ba, Param1 = am, Param2 = ph, Param3 = fr };
        }
        else if (string.Equals(type, "stretch", StringComparison.OrdinalIgnoreCase))
        {
            string? funcName = tokenizer.NextToken();
            int waveFunc = 0; // 0=sin, 1=triangle, 2=square, 3=sawtooth, 4=inverseSawtooth
            if (funcName != null)
            {
                if (string.Equals(funcName, "triangle", StringComparison.OrdinalIgnoreCase)) waveFunc = 1;
                else if (string.Equals(funcName, "square", StringComparison.OrdinalIgnoreCase)) waveFunc = 2;
                else if (string.Equals(funcName, "sawtooth", StringComparison.OrdinalIgnoreCase)) waveFunc = 3;
                else if (string.Equals(funcName, "inverseSawtooth", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(funcName, "inversesawtooth", StringComparison.OrdinalIgnoreCase)) waveFunc = 4;
            }
            string? sb = tokenizer.NextToken();
            string? sa = tokenizer.NextToken();
            string? sp = tokenizer.NextToken();
            string? sf = tokenizer.NextToken();
            float ba2 = 0, am2 = 0, ph2 = 0, fr2 = 0;
            if (sb != null) float.TryParse(sb, ci, out ba2);
            if (sa != null) float.TryParse(sa, ci, out am2);
            if (sp != null) float.TryParse(sp, ci, out ph2);
            if (sf != null) float.TryParse(sf, ci, out fr2);
            return new TcMod { Type = TcModType.Stretch, Param0 = waveFunc, Param1 = ba2, Param2 = am2, Param3 = ph2, Param4 = fr2 };
        }

        return null;
    }

    private static DeformVertexes? ParseDeformVertexes(ref ShaderTokenizer tokenizer)
    {
        string? type = tokenizer.NextToken();
        if (type == null) return null;

        var ci = System.Globalization.CultureInfo.InvariantCulture;

        if (string.Equals(type, "wave", StringComparison.OrdinalIgnoreCase))
        {
            string? divS = tokenizer.NextToken();
            string? funcS = tokenizer.NextToken();
            string? baseS = tokenizer.NextToken();
            string? ampS = tokenizer.NextToken();
            string? phaseS = tokenizer.NextToken();
            string? freqS = tokenizer.NextToken();
            float div = 0, ba = 0, am = 0, ph = 0, fr = 0;
            int func = ParseWaveFunc(funcS);
            if (divS != null) float.TryParse(divS, ci, out div);
            if (baseS != null) float.TryParse(baseS, ci, out ba);
            if (ampS != null) float.TryParse(ampS, ci, out am);
            if (phaseS != null) float.TryParse(phaseS, ci, out ph);
            if (freqS != null) float.TryParse(freqS, ci, out fr);
            return new DeformVertexes { Type = DeformType.Wave, Param0 = div, Param1 = func, Param2 = ba, Param3 = am, Param4 = ph, Param5 = fr };
        }
        else if (string.Equals(type, "move", StringComparison.OrdinalIgnoreCase))
        {
            string? xS = tokenizer.NextToken();
            string? yS = tokenizer.NextToken();
            string? zS = tokenizer.NextToken();
            string? funcS = tokenizer.NextToken();
            string? baseS = tokenizer.NextToken();
            string? ampS = tokenizer.NextToken();
            string? phaseS = tokenizer.NextToken();
            string? freqS = tokenizer.NextToken();
            float x = 0, y = 0, z = 0, ba = 0, am = 0, ph = 0, fr = 0;
            int func = ParseWaveFunc(funcS);
            if (xS != null) float.TryParse(xS, ci, out x);
            if (yS != null) float.TryParse(yS, ci, out y);
            if (zS != null) float.TryParse(zS, ci, out z);
            if (baseS != null) float.TryParse(baseS, ci, out ba);
            if (ampS != null) float.TryParse(ampS, ci, out am);
            if (phaseS != null) float.TryParse(phaseS, ci, out ph);
            if (freqS != null) float.TryParse(freqS, ci, out fr);
            return new DeformVertexes { Type = DeformType.Move, Param0 = x, Param1 = y, Param2 = z, Param4 = func, Param5 = ba, Param6 = am, Param7 = ph, Param8 = fr };
        }
        else if (string.Equals(type, "bulge", StringComparison.OrdinalIgnoreCase))
        {
            string? wS = tokenizer.NextToken();
            string? hS = tokenizer.NextToken();
            string? sS = tokenizer.NextToken();
            float w = 0, h = 0, s = 0;
            if (wS != null) float.TryParse(wS, ci, out w);
            if (hS != null) float.TryParse(hS, ci, out h);
            if (sS != null) float.TryParse(sS, ci, out s);
            return new DeformVertexes { Type = DeformType.Bulge, Param0 = w, Param1 = h, Param2 = s };
        }
        else if (string.Equals(type, "normal", StringComparison.OrdinalIgnoreCase))
        {
            string? aS = tokenizer.NextToken();
            string? fS = tokenizer.NextToken();
            float am = 0, fr = 0;
            if (aS != null) float.TryParse(aS, ci, out am);
            if (fS != null) float.TryParse(fS, ci, out fr);
            return new DeformVertexes { Type = DeformType.Normal, Param0 = am, Param1 = fr };
        }
        else if (string.Equals(type, "autosprite", StringComparison.OrdinalIgnoreCase))
        {
            return new DeformVertexes { Type = DeformType.AutoSprite };
        }
        else if (string.Equals(type, "autosprite2", StringComparison.OrdinalIgnoreCase))
        {
            return new DeformVertexes { Type = DeformType.AutoSprite2 };
        }

        return null;
    }

    private static int ParseWaveFunc(string? name)
    {
        if (name == null) return 0;
        if (string.Equals(name, "sin", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(name, "triangle", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(name, "square", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(name, "sawtooth", StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(name, "inverseSawtooth", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "inversesawtooth", StringComparison.OrdinalIgnoreCase)) return 4;
        if (string.Equals(name, "noise", StringComparison.OrdinalIgnoreCase)) return 5;
        return 0;
    }
    /// - Skips C-style comments (// and /* */)
    /// - Handles quoted strings
    /// - Returns individual tokens delimited by whitespace
    /// - Treats { and } as standalone tokens
    /// </summary>
    private ref struct ShaderTokenizer
    {
        private ReadOnlySpan<char> _text;
        private int _pos;
        private string? _pending;

        public ShaderTokenizer(string text)
        {
            _text = text.AsSpan();
            _pos = 0;
            _pending = null;
        }

        public void PushBack(string token) => _pending = token;

        public bool HasMore()
        {
            if (_pending != null) return true;
            SkipWhitespaceAndComments();
            return _pos < _text.Length;
        }

        public string? NextToken()
        {
            if (_pending != null)
            {
                var t = _pending;
                _pending = null;
                return t;
            }

            SkipWhitespaceAndComments();
            if (_pos >= _text.Length)
                return null;

            // Quoted string
            if (_text[_pos] == '"')
            {
                _pos++;
                int start = _pos;
                while (_pos < _text.Length && _text[_pos] != '"' && _text[_pos] != '\n')
                    _pos++;
                string result = _text[start.._pos].ToString();
                if (_pos < _text.Length && _text[_pos] == '"')
                    _pos++;
                return result;
            }

            // Braces are standalone tokens
            if (_text[_pos] == '{' || _text[_pos] == '}')
            {
                return _text[_pos++..(_pos)].ToString();
            }

            // Regular token
            {
                int start = _pos;
                while (_pos < _text.Length && !char.IsWhiteSpace(_text[_pos])
                       && _text[_pos] != '{' && _text[_pos] != '}')
                    _pos++;
                return _text[start.._pos].ToString();
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < _text.Length)
            {
                // Skip whitespace
                if (char.IsWhiteSpace(_text[_pos]))
                {
                    _pos++;
                    continue;
                }

                // Skip // line comments
                if (_pos + 1 < _text.Length && _text[_pos] == '/' && _text[_pos + 1] == '/')
                {
                    _pos += 2;
                    while (_pos < _text.Length && _text[_pos] != '\n')
                        _pos++;
                    continue;
                }

                // Skip /* block comments */
                if (_pos + 1 < _text.Length && _text[_pos] == '/' && _text[_pos + 1] == '*')
                {
                    _pos += 2;
                    while (_pos + 1 < _text.Length && !(_text[_pos] == '*' && _text[_pos + 1] == '/'))
                        _pos++;
                    if (_pos + 1 < _text.Length)
                        _pos += 2;
                    continue;
                }

                break;
            }
        }
    }
}

/// <summary>
/// Represents a parsed shader definition from a .shader script file.
/// </summary>
public sealed class ShaderDef
{
    /// <summary>Shader name as defined in the script.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Path to the primary image/texture (from the first "map" or "clampMap" directive).
    /// Null if the shader has no usable image stage (e.g. $lightmap only).
    /// </summary>
    public string? ImagePath { get; init; }

    /// <summary>Whether the texture should use clamp-to-edge wrapping.</summary>
    public bool Clamp { get; init; }

    /// <summary>Blend mode from the first stage's blendFunc directive.</summary>
    public BlendMode Blend { get; init; }

    /// <summary>Skybox outer box texture base name from skyparms directive (e.g. "env/tim_dm14/dm14").</summary>
    public string? SkyBox { get; init; }

    /// <summary>Whether the shader has surfaceparm trans (truly transparent).</summary>
    public bool IsTransparent { get; init; }

    /// <summary>Alpha test function: 0=none, 1=GT0 (alpha>0), 2=LT128 (alpha&lt;0.5), 3=GE128 (alpha>=0.5).</summary>
    public int AlphaFunc { get; init; }

    /// <summary>Cull mode: 0=front (default Q3), 1=back, 2=none/twosided.</summary>
    public int CullMode { get; init; }

    /// <summary>Whether any stage uses tcGen environment (reflection mapping).</summary>
    public bool HasEnvMap { get; init; }

    /// <summary>Animation frame image paths (null if not animated).</summary>
    public string[]? AnimFrames { get; init; }

    /// <summary>Animation frequency in frames per second.</summary>
    public float AnimFrequency { get; init; }

    /// <summary>List of tcMod operations to apply in order (null if none).</summary>
    public TcMod[]? TcMods { get; init; }

    /// <summary>RGB generation mode: 0=identity (default), 1=vertex, 2=entity, 3=wave, 4=identityLighting.</summary>
    public int RgbGen { get; init; }

    /// <summary>Alpha generation mode: 0=identity (default), 1=vertex, 2=entity, 3=wave.</summary>
    public int AlphaGen { get; init; }

    /// <summary>Whether polygonOffset is set for this shader (prevents z-fighting on decals).</summary>
    public bool PolygonOffset { get; init; }

    /// <summary>Whether this shader allows surfaces from different entities to be merged (smoke, blood).</summary>
    public bool EntityMergable { get; init; }

    /// <summary>Depth function: 0=lequal (default), 1=equal.</summary>
    public int DepthFunc { get; init; }

    /// <summary>Whether depthWrite is explicitly set on a blended stage.</summary>
    public bool DepthWrite { get; init; }

    /// <summary>Shader sort key: 0=default (auto), 3=opaque, 4=decal, 5=seeThrough, 6=banner, 8+=blend.</summary>
    public int SortKey { get; init; }

    /// <summary>deformVertexes operations (null if none).</summary>
    public DeformVertexes[]? Deforms { get; init; }

    /// <summary>All parsed shader stages (null if no stages or single-stage fallback).</summary>
    public ShaderStage[]? Stages { get; init; }

    /// <summary>Whether this shader has fogParms (defines a fog volume).</summary>
    public bool HasFogParms { get; init; }

    /// <summary>Fog color RGB (0-1) from fogParms directive.</summary>
    public float FogColorR { get; init; }
    public float FogColorG { get; init; }
    public float FogColorB { get; init; }

    /// <summary>Distance at which fog becomes fully opaque.</summary>
    public float FogDepthForOpaque { get; init; }

    /// <summary>surfaceparm nolightmap — surface has no lightmap.</summary>
    public bool NoLightMap { get; init; }

    /// <summary>surfaceparm nodlight — surface ignores dynamic lights.</summary>
    public bool NoDLight { get; init; }

    /// <summary>surfaceparm nomarks — surface doesn't receive impact marks.</summary>
    public bool NoMarks { get; init; }

    /// <summary>Whether nopicmip directive is set (skip picmip quality reduction).</summary>
    public bool NoPicMip { get; init; }

    /// <summary>Whether nomipmaps directive is set (no mipmaps AND no picmip).</summary>
    public bool NoMipMaps { get; init; }

    /// <summary>Whether this is a portal/mirror shader (sort portal or surfaceparm).</summary>
    public bool IsPortal { get; init; }

    /// <summary>Normal map image path from normalMap/bumpMap directive (null if none).</summary>
    public string? NormalMapPath { get; init; }

    /// <summary>Specular map image path from specularMap directive (null if none).</summary>
    public string? SpecularMapPath { get; init; }
}

/// <summary>
/// Represents a deformVertexes operation from a Q3 shader script.
/// </summary>
public struct DeformVertexes
{
    public DeformType Type;
    /// <summary>Wave: div. Move: x. Bulge: width.</summary>
    public float Param0;
    /// <summary>Wave: waveFunc. Move: y. Bulge: height.</summary>
    public float Param1;
    /// <summary>Wave: base. Move: z. Bulge: speed.</summary>
    public float Param2;
    /// <summary>Wave: amplitude.</summary>
    public float Param3;
    /// <summary>Wave: phase. Move: waveFunc.</summary>
    public float Param4;
    /// <summary>Wave: frequency. Move: base.</summary>
    public float Param5;
    /// <summary>Move: amplitude.</summary>
    public float Param6;
    /// <summary>Move: phase.</summary>
    public float Param7;
    /// <summary>Move: frequency.</summary>
    public float Param8;
}

public enum DeformType
{
    Wave,       // deformVertexes wave <div> <func> <base> <amp> <phase> <freq>
    Move,       // deformVertexes move <x> <y> <z> <func> <base> <amp> <phase> <freq>
    Bulge,      // deformVertexes bulge <width> <height> <speed>
    Normal,     // deformVertexes normal <amp> <freq>
    AutoSprite, // deformVertexes autosprite
    AutoSprite2 // deformVertexes autosprite2
}

/// <summary>
/// Represents a single tcMod operation from a Q3 shader script.
/// </summary>
public struct TcMod
{
    public TcModType Type;
    public float Param0, Param1, Param2, Param3, Param4, Param5;
}

public enum TcModType
{
    Scroll,   // Param0=sSpeed, Param1=tSpeed
    Scale,    // Param0=sScale, Param1=tScale
    Rotate,   // Param0=degreesPerSecond
    Turb,     // Param0=base, Param1=amplitude, Param2=phase, Param3=frequency
    Stretch,  // Param0=waveFunc, Param1=base, Param2=amplitude, Param3=phase, Param4=frequency
}

/// <summary>
/// Q3 blend modes storing actual GL blend factors for correct rendering.
/// </summary>
public struct BlendMode : System.IEquatable<BlendMode>
{
    public int SrcFactor;
    public int DstFactor;

    // GL blend factor constants (matching OpenGL)
    public const int GL_ZERO = 0;
    public const int GL_ONE = 1;
    public const int GL_SRC_COLOR = 0x0300;
    public const int GL_ONE_MINUS_SRC_COLOR = 0x0301;
    public const int GL_SRC_ALPHA = 0x0302;
    public const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int GL_DST_ALPHA = 0x0304;
    public const int GL_ONE_MINUS_DST_ALPHA = 0x0305;
    public const int GL_DST_COLOR = 0x0306;
    public const int GL_ONE_MINUS_DST_COLOR = 0x0307;
    public const int GL_SRC_ALPHA_SATURATE = 0x0308;

    public static readonly BlendMode Opaque = new() { SrcFactor = GL_ONE, DstFactor = GL_ZERO };
    public static readonly BlendMode Alpha = new() { SrcFactor = GL_SRC_ALPHA, DstFactor = GL_ONE_MINUS_SRC_ALPHA };
    public static readonly BlendMode Add = new() { SrcFactor = GL_ONE, DstFactor = GL_ONE };
    public static readonly BlendMode Filter = new() { SrcFactor = GL_DST_COLOR, DstFactor = GL_ZERO };

    public bool IsOpaque => SrcFactor == GL_ONE && DstFactor == GL_ZERO;
    public bool NeedsBlending => !IsOpaque;

    public bool Equals(BlendMode other) => SrcFactor == other.SrcFactor && DstFactor == other.DstFactor;
    public override bool Equals(object? obj) => obj is BlendMode other && Equals(other);
    public override int GetHashCode() => System.HashCode.Combine(SrcFactor, DstFactor);
    public static bool operator ==(BlendMode a, BlendMode b) => a.Equals(b);
    public static bool operator !=(BlendMode a, BlendMode b) => !a.Equals(b);
}

/// <summary>
/// Represents a single stage within a Q3 shader script.
/// Q3 shaders can have up to 8 stages, each rendered as a separate draw call
/// with its own texture, blend mode, texcoord generation, and color generation.
/// </summary>
public sealed class ShaderStage
{
    /// <summary>Image path (null for $lightmap or $whiteimage).</summary>
    public string? ImagePath { get; init; }

    /// <summary>True if this is a $lightmap stage.</summary>
    public bool IsLightmap { get; init; }

    /// <summary>True if this is a $whiteimage stage.</summary>
    public bool IsWhiteImage { get; init; }

    /// <summary>Whether to clamp texture coordinates.</summary>
    public bool Clamp { get; init; }

    /// <summary>Blend mode for this stage.</summary>
    public BlendMode Blend { get; init; }

    /// <summary>Alpha test function: 0=none, 1=GT0, 2=LT128, 3=GE128.</summary>
    public int AlphaFunc { get; init; }

    /// <summary>Whether this stage uses tcGen environment.</summary>
    public bool HasEnvMap { get; init; }

    /// <summary>Animation frame paths (null if not animated).</summary>
    public string[]? AnimFrames { get; init; }

    /// <summary>Animation frequency in fps.</summary>
    public float AnimFrequency { get; init; }

    /// <summary>tcMod operations for this stage.</summary>
    public TcMod[]? TcMods { get; init; }

    /// <summary>RGB generation: 0=identity, 1=vertex, 2=entity, 3=wave, 4=identityLighting.</summary>
    public int RgbGen { get; init; }

    /// <summary>Alpha generation: 0=identity, 1=vertex, 2=entity, 3=wave.</summary>
    public int AlphaGen { get; init; }

    /// <summary>Depth function: 0=lequal, 1=equal.</summary>
    public int DepthFunc { get; init; }

    /// <summary>Whether depthWrite is explicitly set.</summary>
    public bool DepthWrite { get; init; }

    /// <summary>Cinematic video handle (0-15) from videoMap directive, or -1 if not a video stage.</summary>
    public int VideoMapHandle { get; init; } = -1;
}
