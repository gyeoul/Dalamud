using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Interface.Internal;
using Dalamud.Utility.Timing;
using ImGuiNET;
using Lumina.Data.Files;
using Serilog;

using static Dalamud.Interface.ImGuiHelpers;

namespace Dalamud.Interface.GameFonts;

/// <summary>
/// Loads game font for use in ImGui.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal class GameFontManager : IServiceType
{
    private static readonly Dictionary<GameFontFamilyAndSize, Tuple<string, string>> FontSet = new()
    {
        { GameFontFamilyAndSize.Axis96,         Tuple.Create("KrnAXIS_120.fdt",     "font_krn_{0}.tex") },
        { GameFontFamilyAndSize.Axis12,         Tuple.Create("KrnAXIS_120.fdt",     "font_krn_{0}.tex") },
        { GameFontFamilyAndSize.Axis14,         Tuple.Create("KrnAXIS_140.fdt",     "font_krn_{0}.tex") },
        { GameFontFamilyAndSize.Axis18,         Tuple.Create("KrnAXIS_180.fdt",     "font_krn_{0}.tex") },
        { GameFontFamilyAndSize.Axis36,         Tuple.Create("KrnAXIS_360.fdt",     "font_krn_{0}.tex") },
        { GameFontFamilyAndSize.Jupiter16,      Tuple.Create("Jupiter_16.fdt",      "font{0}.tex") },
        { GameFontFamilyAndSize.Jupiter20,      Tuple.Create("Jupiter_20.fdt",      "font{0}.tex") },
        { GameFontFamilyAndSize.Jupiter23,      Tuple.Create("Jupiter_23.fdt",      "font{0}.tex") },
        { GameFontFamilyAndSize.Jupiter45,      Tuple.Create("Jupiter_45.fdt",      "font{0}.tex") },
        { GameFontFamilyAndSize.Jupiter46,      Tuple.Create("Jupiter_46.fdt",      "font{0}.tex") },
        { GameFontFamilyAndSize.Jupiter90,      Tuple.Create("Jupiter_90.fdt",      "font{0}.tex") },
        { GameFontFamilyAndSize.Meidinger16,    Tuple.Create("Meidinger_16.fdt",    "font{0}.tex") },
        { GameFontFamilyAndSize.Meidinger20,    Tuple.Create("Meidinger_20.fdt",    "font{0}.tex") },
        { GameFontFamilyAndSize.Meidinger40,    Tuple.Create("Meidinger_40.fdt",    "font{0}.tex") },
        { GameFontFamilyAndSize.MiedingerMid10, Tuple.Create("MiedingerMid_10.fdt", "font{0}.tex") },
        { GameFontFamilyAndSize.MiedingerMid12, Tuple.Create("MiedingerMid_12.fdt", "font{0}.tex") },
        { GameFontFamilyAndSize.MiedingerMid14, Tuple.Create("MiedingerMid_14.fdt", "font{0}.tex") },
        { GameFontFamilyAndSize.MiedingerMid18, Tuple.Create("MiedingerMid_18.fdt", "font{0}.tex") },
        { GameFontFamilyAndSize.MiedingerMid36, Tuple.Create("MiedingerMid_36.fdt", "font{0}.tex") },
        { GameFontFamilyAndSize.TrumpGothic184, Tuple.Create("TrumpGothic_184.fdt", "font{0}.tex") },
        { GameFontFamilyAndSize.TrumpGothic23,  Tuple.Create("TrumpGothic_23.fdt",  "font{0}.tex") },
        { GameFontFamilyAndSize.TrumpGothic34,  Tuple.Create("TrumpGothic_34.fdt",  "font{0}.tex") },
        { GameFontFamilyAndSize.TrumpGothic68,  Tuple.Create("TrumpGothic_68.fdt",  "font{0}.tex") },
    };

    private readonly Dictionary<GameFontFamilyAndSize, FdtReader> fdtMap = new();
    private readonly Dictionary<string, byte[]> textureMap = new();

    private readonly object syncRoot = new();

    private readonly Dictionary<GameFontStyle, ImFontPtr> fonts = new();
    private readonly Dictionary<GameFontStyle, int> fontUseCounter = new();
    private readonly Dictionary<GameFontStyle, Dictionary<char, Tuple<int, FdtReader.FontTableEntry>>> glyphRectIds = new();

#pragma warning disable CS0414
    private bool isBetweenBuildFontsAndRightAfterImGuiIoFontsBuild = false;
#pragma warning restore CS0414

    [ServiceManager.ServiceConstructor]
    private GameFontManager(DataManager dataManager)
    {
        using (Timings.Start("Getting fdt data"))
        {
            foreach (var (font, (fdtFileName, texFileName)) in FontSet) {
                this.fdtMap[font] = new FdtReader(dataManager.GetFile($"common/font/{fdtFileName}")!.Data);
            }
        }

        using (Timings.Start("Getting texture data"))
        {
            var texTasks = this.fdtMap
                .SelectMany(x => x.Value.Glyphs.Select(g => string.Format(FontSet[x.Key].Item2, g.TextureFileIndex + 1)))
                .Distinct()
                .Select(x => Tuple.Create(x, dataManager.GetFile<TexFile>($"common/font/{x}")!))
                .Select(x => Tuple.Create(x.Item1, new Task<byte[]>(Timings.AttachTimingHandle(() => x.Item2.ImageData!))))
                .ToArray();

            foreach (var (texFileName, task) in texTasks)
            {
                task.Start();
            }

            foreach (var (texFileName, task) in texTasks)
            {
                var pixels = task.GetAwaiter().GetResult();
                this.textureMap[texFileName] = pixels;
            }
        }
    }

    /// <summary>
    /// Describe font into a string.
    /// </summary>
    /// <param name="font">Font to describe.</param>
    /// <returns>A string in a form of "FontName (NNNpt)".</returns>
    public static string DescribeFont(GameFontFamilyAndSize font)
    {
        return font switch
        {
            GameFontFamilyAndSize.Undefined => "-",
            GameFontFamilyAndSize.Axis96 => "AXIS (9.6pt)",
            GameFontFamilyAndSize.Axis12 => "AXIS (12pt)",
            GameFontFamilyAndSize.Axis14 => "AXIS (14pt)",
            GameFontFamilyAndSize.Axis18 => "AXIS (18pt)",
            GameFontFamilyAndSize.Axis36 => "AXIS (36pt)",
            GameFontFamilyAndSize.Jupiter16 => "Jupiter (16pt)",
            GameFontFamilyAndSize.Jupiter20 => "Jupiter (20pt)",
            GameFontFamilyAndSize.Jupiter23 => "Jupiter (23pt)",
            GameFontFamilyAndSize.Jupiter45 => "Jupiter Numeric (45pt)",
            GameFontFamilyAndSize.Jupiter46 => "Jupiter (46pt)",
            GameFontFamilyAndSize.Jupiter90 => "Jupiter Numeric (90pt)",
            GameFontFamilyAndSize.Meidinger16 => "Meidinger Numeric (16pt)",
            GameFontFamilyAndSize.Meidinger20 => "Meidinger Numeric (20pt)",
            GameFontFamilyAndSize.Meidinger40 => "Meidinger Numeric (40pt)",
            GameFontFamilyAndSize.MiedingerMid10 => "MiedingerMid (10pt)",
            GameFontFamilyAndSize.MiedingerMid12 => "MiedingerMid (12pt)",
            GameFontFamilyAndSize.MiedingerMid14 => "MiedingerMid (14pt)",
            GameFontFamilyAndSize.MiedingerMid18 => "MiedingerMid (18pt)",
            GameFontFamilyAndSize.MiedingerMid36 => "MiedingerMid (36pt)",
            GameFontFamilyAndSize.TrumpGothic184 => "Trump Gothic (18.4pt)",
            GameFontFamilyAndSize.TrumpGothic23 => "Trump Gothic (23pt)",
            GameFontFamilyAndSize.TrumpGothic34 => "Trump Gothic (34pt)",
            GameFontFamilyAndSize.TrumpGothic68 => "Trump Gothic (68pt)",
            _ => throw new ArgumentOutOfRangeException(nameof(font), font, "Invalid argument"),
        };
    }

    /// <summary>
    /// Determines whether a font should be able to display most of stuff.
    /// </summary>
    /// <param name="font">Font to check.</param>
    /// <returns>True if it can.</returns>
    public static bool IsGenericPurposeFont(GameFontFamilyAndSize font)
    {
        return font switch
        {
            GameFontFamilyAndSize.Axis96 => true,
            GameFontFamilyAndSize.Axis12 => true,
            GameFontFamilyAndSize.Axis14 => true,
            GameFontFamilyAndSize.Axis18 => true,
            GameFontFamilyAndSize.Axis36 => true,
            _ => false,
        };
    }

    /// <summary>
    /// Unscales fonts after they have been rendered onto atlas.
    /// </summary>
    /// <param name="fontPtr">Font to unscale.</param>
    /// <param name="fontScale">Scale factor.</param>
    /// <param name="rebuildLookupTable">Whether to call target.BuildLookupTable().</param>
    public static void UnscaleFont(ImFontPtr fontPtr, float fontScale, bool rebuildLookupTable = true)
    {
        if (fontScale == 1)
            return;

        unsafe
        {
            var font = fontPtr.NativePtr;
            for (int i = 0, i_ = font->IndexedHotData.Size; i < i_; ++i)
            {
                font->IndexedHotData.Ref<ImFontGlyphHotDataReal>(i).AdvanceX /= fontScale;
                font->IndexedHotData.Ref<ImFontGlyphHotDataReal>(i).OccupiedWidth /= fontScale;
            }

            font->FontSize /= fontScale;
            font->Ascent /= fontScale;
            font->Descent /= fontScale;
            if (font->ConfigData != null)
                font->ConfigData->SizePixels /= fontScale;
            var glyphs = (ImFontGlyphReal*)font->Glyphs.Data;
            for (int i = 0, i_ = font->Glyphs.Size; i < i_; i++)
            {
                var glyph = &glyphs[i];
                glyph->X0 /= fontScale;
                glyph->X1 /= fontScale;
                glyph->Y0 /= fontScale;
                glyph->Y1 /= fontScale;
                glyph->AdvanceX /= fontScale;
            }

            for (int i = 0, i_ = font->KerningPairs.Size; i < i_; i++)
                font->KerningPairs.Ref<ImFontKerningPair>(i).AdvanceXAdjustment /= fontScale;
            for (int i = 0, i_ = font->FrequentKerningPairs.Size; i < i_; i++)
                font->FrequentKerningPairs.Ref<float>(i) /= fontScale;
        }

        if (rebuildLookupTable && fontPtr.Glyphs.Size > 0)
            fontPtr.BuildLookupTable();
    }

    /// <summary>
    /// Creates a new GameFontHandle, and increases internal font reference counter, and if it's first time use, then the font will be loaded on next font building process.
    /// </summary>
    /// <param name="style">Font to use.</param>
    /// <returns>Handle to game font that may or may not be ready yet.</returns>
    public GameFontHandle NewFontRef(GameFontStyle style)
    {
        var interfaceManager = Service<InterfaceManager>.Get();
        var needRebuild = false;

        lock (this.syncRoot)
        {
            this.fontUseCounter[style] = this.fontUseCounter.GetValueOrDefault(style, 0) + 1;
        }

        needRebuild = !this.fonts.ContainsKey(style);
        if (needRebuild)
        {
            Log.Information("[GameFontManager] NewFontRef: Queueing RebuildFonts because {0} has been requested.", style.ToString());
            Service<Framework>.GetAsync()
                              .ContinueWith(task => task.Result.RunOnTick(() => interfaceManager.RebuildFonts()));
        }

        return new(this, style);
    }

    /// <summary>
    /// Gets the font.
    /// </summary>
    /// <param name="style">Font to get.</param>
    /// <returns>Corresponding font or null.</returns>
    public ImFontPtr? GetFont(GameFontStyle style) => this.fonts.GetValueOrDefault(style, null);

    /// <summary>
    /// Gets the corresponding FdtReader.
    /// </summary>
    /// <param name="family">Font to get.</param>
    /// <returns>Corresponding FdtReader or null.</returns>
    public FdtReader? GetFdtReader(GameFontFamilyAndSize family) => this.fdtMap[family];

    /// <summary>
    /// Fills missing glyphs in target font from source font, if both are not null.
    /// </summary>
    /// <param name="source">Source font.</param>
    /// <param name="target">Target font.</param>
    /// <param name="missingOnly">Whether to copy missing glyphs only.</param>
    /// <param name="rebuildLookupTable">Whether to call target.BuildLookupTable().</param>
    public void CopyGlyphsAcrossFonts(ImFontPtr? source, GameFontStyle target, bool missingOnly, bool rebuildLookupTable)
    {
        ImGuiHelpers.CopyGlyphsAcrossFonts(source, this.fonts[target], missingOnly, rebuildLookupTable);
    }

    /// <summary>
    /// Fills missing glyphs in target font from source font, if both are not null.
    /// </summary>
    /// <param name="source">Source font.</param>
    /// <param name="target">Target font.</param>
    /// <param name="missingOnly">Whether to copy missing glyphs only.</param>
    /// <param name="rebuildLookupTable">Whether to call target.BuildLookupTable().</param>
    public void CopyGlyphsAcrossFonts(GameFontStyle source, ImFontPtr? target, bool missingOnly, bool rebuildLookupTable)
    {
        ImGuiHelpers.CopyGlyphsAcrossFonts(this.fonts[source], target, missingOnly, rebuildLookupTable);
    }

    /// <summary>
    /// Fills missing glyphs in target font from source font, if both are not null.
    /// </summary>
    /// <param name="source">Source font.</param>
    /// <param name="target">Target font.</param>
    /// <param name="missingOnly">Whether to copy missing glyphs only.</param>
    /// <param name="rebuildLookupTable">Whether to call target.BuildLookupTable().</param>
    public void CopyGlyphsAcrossFonts(GameFontStyle source, GameFontStyle target, bool missingOnly, bool rebuildLookupTable)
    {
        ImGuiHelpers.CopyGlyphsAcrossFonts(this.fonts[source], this.fonts[target], missingOnly, rebuildLookupTable);
    }

    /// <summary>
    /// Build fonts before plugins do something more. To be called from InterfaceManager.
    /// </summary>
    public void BuildFonts()
    {
        this.isBetweenBuildFontsAndRightAfterImGuiIoFontsBuild = true;

        this.glyphRectIds.Clear();
        this.fonts.Clear();

        lock (this.syncRoot)
        {
            foreach (var style in this.fontUseCounter.Keys)
                this.EnsureFont(style);
        }
    }

    /// <summary>
    /// Record that ImGui.GetIO().Fonts.Build() has been called.
    /// </summary>
    public void AfterIoFontsBuild()
    {
        this.isBetweenBuildFontsAndRightAfterImGuiIoFontsBuild = false;
    }

    /// <summary>
    /// Checks whether GameFontMamager owns an ImFont.
    /// </summary>
    /// <param name="fontPtr">ImFontPtr to check.</param>
    /// <returns>Whether it owns.</returns>
    public bool OwnsFont(ImFontPtr fontPtr) => this.fonts.ContainsValue(fontPtr);

    /// <summary>
    /// Post-build fonts before plugins do something more. To be called from InterfaceManager.
    /// </summary>
    public unsafe void AfterBuildFonts()
    {
        var interfaceManager = Service<InterfaceManager>.Get();
        var ioFonts = ImGui.GetIO().Fonts;
        var fontGamma = interfaceManager.FontGamma;

        var pixels8s = new byte*[ioFonts.Textures.Size];
        var pixels32s = new uint*[ioFonts.Textures.Size];
        var widths = new int[ioFonts.Textures.Size];
        var heights = new int[ioFonts.Textures.Size];
        for (var i = 0; i < pixels8s.Length; i++)
        {
            ioFonts.GetTexDataAsRGBA32(i, out pixels8s[i], out widths[i], out heights[i]);
            pixels32s[i] = (uint*)pixels8s[i];
        }

        foreach (var (style, font) in this.fonts)
        {
            var fdt = this.fdtMap[style.FamilyAndSize];
            var scale = style.SizePt / fdt.FontHeader.Size;
            var fontPtr = font.NativePtr;

            Log.Verbose("[GameFontManager] AfterBuildFonts: Scaling {0} from {1}pt to {2}pt (scale: {3})", style.ToString(), fdt.FontHeader.Size, style.SizePt, scale);

            fontPtr->FontSize = fdt.FontHeader.Size * 4 / 3;
            if (fontPtr->ConfigData != null)
                fontPtr->ConfigData->SizePixels = fontPtr->FontSize;
            fontPtr->Ascent = fdt.FontHeader.Ascent;
            fontPtr->Descent = fdt.FontHeader.Descent;
            fontPtr->EllipsisChar = '…';
            foreach (var fallbackCharCandidate in "〓?!")
            {
                var glyph = font.FindGlyphNoFallback(fallbackCharCandidate);
                if ((IntPtr)glyph.NativePtr != IntPtr.Zero)
                {
                    var ptr = font.NativePtr;
                    ptr->FallbackChar = fallbackCharCandidate;
                    ptr->FallbackGlyph = glyph.NativePtr;
                    ptr->FallbackHotData = (ImFontGlyphHotData*)ptr->IndexedHotData.Address<ImFontGlyphHotDataReal>(fallbackCharCandidate);
                    break;
                }
            }

            // I have no idea what's causing NPE, so just to be safe
            try
            {
                if (font.NativePtr != null && font.NativePtr->ConfigData != null)
                {
                    var nameBytes = Encoding.UTF8.GetBytes(style.ToString() + "\0");
                    Marshal.Copy(nameBytes, 0, (IntPtr)font.ConfigData.Name.Data, Math.Min(nameBytes.Length, font.ConfigData.Name.Count));
                }
            }
            catch (NullReferenceException)
            {
                // do nothing
            }

            foreach (var (c, (rectId, glyph)) in this.glyphRectIds[style])
            {
                var rc = (ImFontAtlasCustomRectReal*)ioFonts.GetCustomRectByIndex(rectId).NativePtr;
                var pixels8 = pixels8s[rc->TextureIndex];
                var pixels32 = pixels32s[rc->TextureIndex];
                var width = widths[rc->TextureIndex];
                var height = heights[rc->TextureIndex];

                var texFileName = string.Format(FontSet[style.FamilyAndSize].Item2, glyph.TextureFileIndex + 1);
                var sourceBuffer = this.textureMap[texFileName];

                var sourceBufferDelta = glyph.TextureChannelByteIndex;
                var widthAdjustment = style.CalculateBaseWidthAdjustment(fdt, glyph);
                if (widthAdjustment == 0)
                {
                    for (var y = 0; y < glyph.BoundingHeight; y++)
                    {
                        for (var x = 0; x < glyph.BoundingWidth; x++)
                        {
                            var a = sourceBuffer[sourceBufferDelta + (4 * (((glyph.TextureOffsetY + y) * fdt.FontHeader.TextureWidth) + glyph.TextureOffsetX + x))];
                            pixels32[((rc->Y + y) * width) + rc->X + x] = (uint)(a << 24) | 0xFFFFFFu;
                        }
                    }
                }
                else
                {
                    for (var y = 0; y < glyph.BoundingHeight; y++)
                    {
                        for (var x = 0; x < glyph.BoundingWidth + widthAdjustment; x++)
                            pixels32[((rc->Y + y) * width) + rc->X + x] = 0xFFFFFFu;
                    }

                    for (int xbold = 0, xbold_ = Math.Max(1, (int)Math.Ceiling(style.Weight + 1)); xbold < xbold_; xbold++)
                    {
                        var boldStrength = Math.Min(1f, style.Weight + 1 - xbold);
                        for (var y = 0; y < glyph.BoundingHeight; y++)
                        {
                            float xDelta = xbold;
                            if (style.BaseSkewStrength > 0)
                                xDelta += style.BaseSkewStrength * (fdt.FontHeader.LineHeight - glyph.CurrentOffsetY - y) / fdt.FontHeader.LineHeight;
                            else if (style.BaseSkewStrength < 0)
                                xDelta -= style.BaseSkewStrength * (glyph.CurrentOffsetY + y) / fdt.FontHeader.LineHeight;
                            var xDeltaInt = (int)Math.Floor(xDelta);
                            var xness = xDelta - xDeltaInt;
                            for (var x = 0; x < glyph.BoundingWidth; x++)
                            {
                                var sourcePixelIndex = ((glyph.TextureOffsetY + y) * fdt.FontHeader.TextureWidth) + glyph.TextureOffsetX + x;
                                var a1 = sourceBuffer[sourceBufferDelta + (4 * sourcePixelIndex)];
                                var a2 = x == glyph.BoundingWidth - 1 ? 0 : sourceBuffer[sourceBufferDelta + (4 * (sourcePixelIndex + 1))];
                                var n = (a1 * xness) + (a2 * (1 - xness));
                                var targetOffset = ((rc->Y + y) * width) + rc->X + x + xDeltaInt;
                                pixels8[(targetOffset * 4) + 3] = Math.Max(pixels8[(targetOffset * 4) + 3], (byte)(boldStrength * n));
                            }
                        }
                    }
                }

                if (Math.Abs(fontGamma - 1.4f) >= 0.001)
                {
                    // Gamma correction (stbtt/FreeType would output in linear space whereas most real world usages will apply 1.4 or 1.8 gamma; Windows/XIV prebaked uses 1.4)
                    for (int y = rc->Y, y_ = rc->Y + rc->Height; y < y_; y++)
                    {
                        for (int x = rc->X, x_ = rc->X + rc->Width; x < x_; x++)
                        {
                            var i = (((y * width) + x) * 4) + 3;
                            pixels8[i] = (byte)(Math.Pow(pixels8[i] / 255.0f, 1.4f / fontGamma) * 255.0f);
                        }
                    }
                }
            }

            UnscaleFont(font, 1 / scale, false);
        }
    }

    /// <summary>
    /// Decrease font reference counter.
    /// </summary>
    /// <param name="style">Font to release.</param>
    internal void DecreaseFontRef(GameFontStyle style)
    {
        lock (this.syncRoot)
        {
            if (!this.fontUseCounter.ContainsKey(style))
                return;

            if ((this.fontUseCounter[style] -= 1) == 0)
                this.fontUseCounter.Remove(style);
        }
    }

    private unsafe void EnsureFont(GameFontStyle style)
    {
        var rectIds = this.glyphRectIds[style] = new();

        if (!this.fdtMap.ContainsKey(style.FamilyAndSize)) {
            return;
        }

        var fdt = this.fdtMap[style.FamilyAndSize];

        ImFontConfigPtr fontConfig = ImGuiNative.ImFontConfig_ImFontConfig();
        fontConfig.OversampleH = 1;
        fontConfig.OversampleV = 1;
        fontConfig.PixelSnapH = false;

        var io = ImGui.GetIO();
        var font = io.Fonts.AddFontDefault(fontConfig);

        fontConfig.Destroy();

        this.fonts[style] = font;
        foreach (var glyph in fdt.Glyphs)
        {
            var c = glyph.Char;
            if (c < 32 || c >= 0xFFFF)
                continue;

            var widthAdjustment = style.CalculateBaseWidthAdjustment(fdt, glyph);
            rectIds[c] = Tuple.Create(
                io.Fonts.AddCustomRectFontGlyph(
                    font,
                    c,
                    glyph.BoundingWidth + widthAdjustment,
                    glyph.BoundingHeight,
                    glyph.AdvanceWidth,
                    new Vector2(0, glyph.CurrentOffsetY)),
                glyph);
        }

        foreach (var kernPair in fdt.Distances)
            font.AddKerningPair(kernPair.Left, kernPair.Right, kernPair.RightOffset);
    }
}
