using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static class SolidWorksTransparentPerspectiveRender
{
    private const string RenderOutputDirectory = "render_3D";

    private sealed class Options
    {
        public string InputDir = "";
        public string InputFile = "";
        public string OutputRoot = Path.Combine(System.Environment.CurrentDirectory, "output_3d");
        public string PartTemplate = "";
        public string AssemblyTemplate = "";
        public int Width = 1400;
        public int Height = 1000;
        public int MaxProcessed = int.MaxValue;
        public bool Recursive;
        public bool KeepBmp;
        public bool SkipExisting;
        public bool Visible;
        public bool CloseWhenDone;
        public bool ShowHelp;
    }

    private sealed class OpenResult
    {
        public ModelDoc2 Model;
        public string Method = "";
        public int Errors;
        public int Warnings;
        public readonly List<string> Attempts = new List<string>();
    }

    private sealed class RenderStyle
    {
        public string Key;
        public string Description;
        public int DisplayMode;
        public bool TransparentMaterial;

        public RenderStyle(string key, string description, int displayMode, bool transparentMaterial)
        {
            Key = key;
            Description = description;
            DisplayMode = displayMode;
            TransparentMaterial = transparentMaterial;
        }
    }

    private static readonly RenderStyle[] BaseStyles = new[]
    {
        new RenderStyle(
            "transparent_shaded_edges_perspective",
            "Perspective isometric viewport, translucent pale material, shaded with visible edges.",
            (int)swDisplayMode_e.swSHADED_EDGES,
            true),
        new RenderStyle(
            "hlg_perspective",
            "Perspective isometric viewport, SOLIDWORKS hidden-lines-grayed display.",
            (int)swDisplayMode_e.swHIDDEN_GREYED,
            false)
    };

    private static readonly RenderStyle CompositeStyle = new RenderStyle(
        "hlg_translucent_faces_perspective",
        "Composite of HLG perspective lines with faint translucent shaded faces.",
        (int)swDisplayMode_e.swHIDDEN_GREYED,
        false);

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            ValidateOptions(options);
            EnsureFolders(options.OutputRoot, options.KeepBmp);
            var logPath = Path.Combine(options.OutputRoot, "logs", "transparent_perspective_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            using (var log = new StreamWriter(logPath))
            {
                return Run(options, log);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 2;
        }
    }

    private static int Run(Options options, StreamWriter log)
    {
        var modelFiles = GetInputFiles(options);
        if (modelFiles.Length == 0)
        {
            Write(log, "No renderable model files found.");
            return 1;
        }

        SldWorks sw = null;
        var preexistingSolidWorksProcessIds = new HashSet<int>(ProcessIdsByName("SLDWORKS"));
        var solidWorksProcessId = 0;
        var ownsSolidWorksProcess = false;
        var temporaryBmpRoot = options.KeepBmp
            ? ""
            : Path.Combine(Path.GetTempPath(), "SolidWorksTransparentPerspectiveRender", Guid.NewGuid().ToString("N"));
        var resultRows = new List<string>();
        resultRows.Add("Status,ModelPath,InputKind,OpenMethod,Style,StyleDescription,PngPath,BmpPath,WidthPx,HeightPx,Message");

        try
        {
            sw = new SldWorks();
            try
            {
                solidWorksProcessId = sw.GetProcessID();
                ownsSolidWorksProcess = solidWorksProcessId > 0 && !preexistingSolidWorksProcessIds.Contains(solidWorksProcessId);
            }
            catch { }

            // SOLIDWORKS bitmap capture is viewport-based; making the app
            // visible is the most reliable way to get nonblank 3D renders.
            sw.Visible = true;
            Write(log, "SOLIDWORKS revision: " + sw.RevisionNumber());
            Write(log, "RenderSize=" + options.Width + "x" + options.Height + " Perspective=true View=isometric");
            Write(log, "OutputRoot=" + options.OutputRoot);
            SetTemplatePreferences(sw, options);
            SetCleanViewportPreferences(sw, log);

            var processed = 0;
            var ok = 0;
            var skippedExisting = 0;
            var failed = 0;

            foreach (var modelPath in modelFiles)
            {
                if (processed >= options.MaxProcessed) break;
                processed++;

                var stem = Path.GetFileNameWithoutExtension(modelPath);
                var transparentPngPath = RenderPngPath(options.OutputRoot, stem, BaseStyles[0]);
                var hlgPngPath = RenderPngPath(options.OutputRoot, stem, BaseStyles[1]);
                var compositePngPath = RenderPngPath(options.OutputRoot, stem, CompositeStyle);
                if (options.SkipExisting && File.Exists(transparentPngPath) && File.Exists(hlgPngPath) && File.Exists(compositePngPath))
                {
                    Write(log, "SKIPPED existing 3D PNGs for " + modelPath);
                    resultRows.Add(ResultCsv("skipped_existing", modelPath, "", "", BaseStyles[0], transparentPngPath, "", options, ""));
                    resultRows.Add(ResultCsv("skipped_existing", modelPath, "", "", BaseStyles[1], hlgPngPath, "", options, ""));
                    resultRows.Add(ResultCsv("skipped_existing", modelPath, "", "", CompositeStyle, compositePngPath, "", options, ""));
                    skippedExisting++;
                    continue;
                }

                Write(log, "Processing " + modelPath);
                var open = OpenModel(sw, modelPath, log);
                if (open.Model == null)
                {
                    var message = "Could not open model: " + string.Join(" | ", open.Attempts.ToArray());
                    Write(log, "FAILED " + message);
                    resultRows.Add(ResultCsv("open_failed", modelPath, GetInputKind(modelPath), open.Method, BaseStyles[0], "", "", options, message));
                    resultRows.Add(ResultCsv("open_failed", modelPath, GetInputKind(modelPath), open.Method, BaseStyles[1], "", "", options, message));
                    resultRows.Add(ResultCsv("open_failed", modelPath, GetInputKind(modelPath), open.Method, CompositeStyle, "", "", options, message));
                    failed++;
                    continue;
                }

                var modelHadFailure = false;
                try
                {
                    foreach (var style in BaseStyles)
                    {
                        var pngPath = RenderPngPath(options.OutputRoot, stem, style);
                        var bmpPath = RenderBmpPath(options, temporaryBmpRoot, stem, style);
                        var manifestBmpPath = options.KeepBmp ? bmpPath : "";
                        var status = "ok";
                        var message = "";
                        try
                        {
                            CaptureStyle(sw, open.Model, style, bmpPath, pngPath, options.Width, options.Height, options.KeepBmp, log);
                        }
                        catch (Exception ex)
                        {
                            status = "failed";
                            message = CleanMessage(ex.Message);
                            modelHadFailure = true;
                            Write(log, "FAILED " + stem + " " + style.Key + ": " + message);
                        }

                        resultRows.Add(ResultCsv(status, modelPath, GetInputKind(modelPath), open.Method, style, pngPath, manifestBmpPath, options, message));
                    }

                    var compositeStatus = "ok";
                    var compositeMessage = "";
                    try
                    {
                        CreateHlgTranslucentComposite(transparentPngPath, hlgPngPath, compositePngPath);
                    }
                    catch (Exception ex)
                    {
                        compositeStatus = "failed";
                        compositeMessage = CleanMessage(ex.Message);
                        modelHadFailure = true;
                        Write(log, "FAILED " + stem + " " + CompositeStyle.Key + ": " + compositeMessage);
                    }
                    resultRows.Add(ResultCsv(compositeStatus, modelPath, GetInputKind(modelPath), open.Method, CompositeStyle, compositePngPath, "", options, compositeMessage));
                }
                finally
                {
                    TryClose(sw, open.Model);
                }

                if (modelHadFailure) failed++;
                else ok++;
            }

            var csvPath = Path.Combine(options.OutputRoot, "manifests", "transparent_perspective_results_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            File.WriteAllLines(csvPath, resultRows.ToArray());
            Write(log, "Done. OK=" + ok + " SkippedExisting=" + skippedExisting + " Failed=" + failed + " Processed=" + processed + " Results=" + csvPath);
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            if (sw != null)
            {
                if (options.CloseWhenDone)
                {
                    try { sw.CloseAllDocuments(true); } catch { }
                    try { sw.ExitApp(); } catch { }
                    if (ownsSolidWorksProcess && solidWorksProcessId > 0)
                    {
                        try
                        {
                            using (var process = System.Diagnostics.Process.GetProcessById(solidWorksProcessId))
                            {
                                if (!process.WaitForExit(5000)) process.Kill();
                            }
                        }
                        catch { }
                    }
                }
                try { Marshal.FinalReleaseComObject(sw); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            if (!options.KeepBmp)
            {
                TryDeleteDirectory(temporaryBmpRoot);
                TryDeleteEmptyBmpRawDirectory(options.OutputRoot);
            }
        }
    }

    private static void CaptureStyle(SldWorks sw, ModelDoc2 model, RenderStyle style, string bmpPath, string pngPath, int width, int height, bool keepBmp, StreamWriter log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bmpPath));
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
        SetCleanViewportPreferences(sw, log);

        var view = GetActiveView(model);
        if (view != null)
        {
            try { view.RemovePerspective(); } catch { }
        }

        if (style.TransparentMaterial) ApplyTransparentAppearance(model, log);
        else TryClearTransparency(model);

        model.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
        model.ViewZoomtofit2();

        view = GetActiveView(model);
        if (view != null)
        {
            try { view.AddPerspective(); } catch { }
            try { view.DisplayMode = style.DisplayMode; } catch { }
        }

        if (style.DisplayMode == (int)swDisplayMode_e.swHIDDEN_GREYED)
        {
            try { model.ViewDisplayHiddengreyed(); } catch { }
        }
        else if (style.DisplayMode == (int)swDisplayMode_e.swSHADED_EDGES)
        {
            try { if (view != null) view.DisplayMode = style.DisplayMode; } catch { }
        }

        try { model.GraphicsRedraw2(); } catch { }
        try { model.ForceRebuild3(false); } catch { }
        model.ViewZoomtofit2();

        var ok = model.SaveBMP(bmpPath, width, height);
        if (!ok || !File.Exists(bmpPath))
        {
            throw new InvalidOperationException("SaveBMP failed.");
        }

        ConvertBmpToPng(bmpPath, pngPath, log);
        if (!File.Exists(pngPath))
        {
            throw new InvalidOperationException("PNG conversion failed.");
        }
        if (!keepBmp)
        {
            try { File.Delete(bmpPath); } catch { }
        }
    }

    private static void CreateHlgTranslucentComposite(string transparentPngPath, string hlgPngPath, string outputPngPath)
    {
        if (!File.Exists(transparentPngPath)) throw new FileNotFoundException("Transparent PNG missing.", transparentPngPath);
        if (!File.Exists(hlgPngPath)) throw new FileNotFoundException("HLG PNG missing.", hlgPngPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath));
        using (var transparent = new Bitmap(transparentPngPath))
        using (var hlg = new Bitmap(hlgPngPath))
        using (var output = new Bitmap(hlg.Width, hlg.Height, PixelFormat.Format24bppRgb))
        {
            for (var y = 0; y < hlg.Height; y++)
            {
                for (var x = 0; x < hlg.Width; x++)
                {
                    var face = x < transparent.Width && y < transparent.Height ? transparent.GetPixel(x, y) : Color.White;
                    var line = hlg.GetPixel(x, y);
                    var faceLuma = Luma(face);
                    var lineLuma = Luma(line);
                    var faceAlpha = faceLuma < 248 ? 0.22 : 0.0;
                    var baseColor = Blend(Color.White, face, faceAlpha);
                    output.SetPixel(x, y, lineLuma < 248 ? Blend(baseColor, line, lineLuma < 190 ? 0.92 : 0.55) : baseColor);
                }
            }
            output.Save(outputPngPath, ImageFormat.Png);
        }
    }

    private static void ApplyTransparentAppearance(ModelDoc2 model, StreamWriter log)
    {
        var material = new double[] { 0.90, 0.96, 1.00, 0.25, 0.75, 0.20, 0.25, 0.82, 0.00 };
        try { model.MaterialPropertyValues = material; } catch (Exception ex) { Write(log, "  model material failed: " + ex.Message); }
        try { model.Extension.SetTopLevelTransparency(true); } catch (Exception ex) { Write(log, "  top-level transparency failed: " + ex.Message); }

        try
        {
            if (model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                var part = (PartDoc)model;
                ApplyMaterialToBodies(part.GetBodies2((int)swBodyType_e.swSolidBody, true), material);
                ApplyMaterialToBodies(part.GetBodies2((int)swBodyType_e.swSheetBody, true), material);
            }
        }
        catch (Exception ex)
        {
            Write(log, "  body material failed: " + ex.Message);
        }
    }

    private static void ApplyMaterialToBodies(object bodiesObj, double[] material)
    {
        if (bodiesObj == null) return;
        var bodies = bodiesObj as object[];
        if (bodies != null)
        {
            foreach (var item in bodies)
            {
                var body = item as Body2;
                if (body != null)
                {
                    try { body.MaterialPropertyValues2 = material; } catch { }
                }
            }
            return;
        }

        var single = bodiesObj as Body2;
        if (single != null)
        {
            try { single.MaterialPropertyValues2 = material; } catch { }
        }
    }

    private static void TryClearTransparency(ModelDoc2 model)
    {
        try { model.Extension.SetTopLevelTransparency(false); } catch { }
    }

    private static void SetCleanViewportPreferences(SldWorks sw, StreamWriter log)
    {
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayReferenceTriad, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayOrigins, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayAxes, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayTemporaryAxes, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayTempAxesOnMouseHover, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayPlanes, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayReferencePoints, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swShowRefGeomName, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayShadowsInShadedMode, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swLargeAsmModeShadowsShadedMode, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayAmbientOcclusionShadows, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDraftQualityAmbientOcclusion, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swDisplayRealViewGraphics, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swColorsGradientPartBackground, false, log);
        TrySetToggle(sw, swUserPreferenceToggle_e.swColorsMatchViewAndFeatureManagerBackground, false, log);
        TrySetInteger(sw, swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance, (int)swColorsBackgroundAppearance_e.swColorsBackgroundAppearance_Plain, log);
        TrySetInteger(sw, swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground, ColorTranslator.ToWin32(Color.White), log);
        TrySetInteger(sw, swUserPreferenceIntegerValue_e.swSystemColorsBackground, ColorTranslator.ToWin32(Color.White), log);
    }

    private static void TrySetToggle(SldWorks sw, swUserPreferenceToggle_e key, bool value, StreamWriter log)
    {
        try { sw.SetUserPreferenceToggle((int)key, value); }
        catch (Exception ex) { Write(log, "  preference toggle failed " + key + ": " + ex.Message); }
    }

    private static void TrySetInteger(SldWorks sw, swUserPreferenceIntegerValue_e key, int value, StreamWriter log)
    {
        try { sw.SetUserPreferenceIntegerValue((int)key, value); }
        catch (Exception ex) { Write(log, "  preference integer failed " + key + ": " + ex.Message); }
    }

    private static ModelView GetActiveView(ModelDoc2 model)
    {
        try { return (ModelView)model.ActiveView; } catch { }
        try { return model.IActiveView; } catch { return null; }
    }

    private static OpenResult OpenModel(SldWorks sw, string path, StreamWriter log)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".sldprt" || extension == ".sldasm")
        {
            return OpenNative(sw, path, log);
        }

        return OpenStep(sw, path, log);
    }

    private static OpenResult OpenNative(SldWorks sw, string nativePath, StreamWriter log)
    {
        var result = new OpenResult();
        var ext = Path.GetExtension(nativePath).ToLowerInvariant();
        var docType = ext == ".sldasm" ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
        var errors = 0;
        var warnings = 0;
        var model = sw.OpenDoc6(nativePath, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
        result.Errors = errors;
        result.Warnings = warnings;
        result.Method = "OpenDoc6 native";
        result.Model = model;
        result.Attempts.Add(result.Method + ": errors=" + errors + " warnings=" + warnings + " opened=" + (model != null));
        Write(log, "  " + result.Attempts[result.Attempts.Count - 1]);
        return result;
    }

    private static OpenResult OpenStep(SldWorks sw, string path, StreamWriter log)
    {
        var result = new OpenResult();
        TrySetImportPreferences(sw);
        TryOpenDoc6(sw, path, (int)swDocumentTypes_e.swDocPART, "", "OpenDoc6 part", result, log);
        if (result.Model == null) TryOpenDoc6(sw, path, (int)swDocumentTypes_e.swDocASSEMBLY, "", "OpenDoc6 assembly", result, log);
        if (result.Model == null) TryOpenDoc6(sw, path, (int)swDocumentTypes_e.swDocPART, "r", "OpenDoc6 part+r", result, log);
        if (result.Model == null) TryOpenDoc6(sw, path, (int)swDocumentTypes_e.swDocPART, "swStepAP214", "OpenDoc6 part AP214", result, log);
        if (result.Model == null) TryLoadFile4(sw, path, "", "LoadFile4", result, log);
        return result;
    }

    private static void TryOpenDoc6(SldWorks sw, string path, int docType, string config, string name, OpenResult result, StreamWriter log)
    {
        try
        {
            var errors = 0;
            var warnings = 0;
            var model = sw.OpenDoc6(path, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, config, ref errors, ref warnings);
            result.Errors = errors;
            result.Warnings = warnings;
            result.Attempts.Add(name + ": errors=" + errors + " warnings=" + warnings + " opened=" + (model != null));
            Write(log, "  " + result.Attempts[result.Attempts.Count - 1]);
            if (model != null)
            {
                result.Model = model;
                result.Method = name;
            }
        }
        catch (Exception ex)
        {
            result.Attempts.Add(name + ": exception=" + ex.Message);
            Write(log, "  " + result.Attempts[result.Attempts.Count - 1]);
        }
    }

    private static void TryLoadFile4(SldWorks sw, string path, string arg, string name, OpenResult result, StreamWriter log)
    {
        try
        {
            var importData = (ImportStepData)sw.GetImportFileData(path);
            importData.MapConfigurationData = true;
            var errors = 0;
            var model = sw.LoadFile4(path, arg, importData, ref errors);
            result.Errors = errors;
            result.Warnings = 0;
            result.Attempts.Add(name + ": errors=" + errors + " opened=" + (model != null));
            Write(log, "  " + result.Attempts[result.Attempts.Count - 1]);
            if (model != null)
            {
                result.Model = model;
                result.Method = name;
            }
        }
        catch (Exception ex)
        {
            result.Attempts.Add(name + ": exception=" + ex.Message);
            Write(log, "  " + result.Attempts[result.Attempts.Count - 1]);
        }
    }

    private static void TrySetImportPreferences(SldWorks sw)
    {
        try { sw.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect, false); } catch { }
        try { sw.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swImportNeutral_SolidandSurface, true); } catch { }
        try { sw.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swImportAutoRunImportDiagnostics, false); } catch { }
        try { sw.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swImportAutoRunImportDiagnosticsPersist, false); } catch { }
        try { sw.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swImportNeutralRunDiagnostics, false); } catch { }
        try { sw.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swForceEnableImportDiagnosis, false); } catch { }
        try { sw.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportCheckAndRepair, 0); } catch { }
        try { sw.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportNeutral_KnitOption, (int)swImportNeutralKnitOption_e.swImportNeutralKnitOption_FormSolids); } catch { }
        try { sw.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportNeutralUnits, (int)swImportNeutralUnits_e.swImportNeutralUnits_ImportFileUnits); } catch { }
    }

    private static void SetTemplatePreferences(SldWorks sw, Options options)
    {
        if (File.Exists(options.PartTemplate))
        {
            try { sw.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart, options.PartTemplate); } catch { }
        }
        if (File.Exists(options.AssemblyTemplate))
        {
            try { sw.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly, options.AssemblyTemplate); } catch { }
        }
    }

    private static void ConvertBmpToPng(string bmpPath, string pngPath, StreamWriter log)
    {
        using (var source = new Bitmap(bmpPath))
        using (var cleaned = ScrubBlueAxisArtifacts(source))
        using (var centered = CenterProjectedForeground(cleaned, log))
        {
            centered.Save(pngPath, ImageFormat.Png);
        }
    }

    private static Bitmap CenterProjectedForeground(Bitmap source, StreamWriter log)
    {
        var bounds = FindProjectedForegroundBounds(source);
        if (bounds.IsEmpty)
        {
            Write(log, "  projected foreground centering skipped: no non-background pixels found");
            return CloneAsRgb(source);
        }

        var sourceCenterX = bounds.Left + (bounds.Width - 1) / 2.0;
        var sourceCenterY = bounds.Top + (bounds.Height - 1) / 2.0;
        var canvasCenterX = (source.Width - 1) / 2.0;
        var canvasCenterY = (source.Height - 1) / 2.0;
        var offsetX = (int)Math.Round(canvasCenterX - sourceCenterX, MidpointRounding.ToEven);
        var offsetY = (int)Math.Round(canvasCenterY - sourceCenterY, MidpointRounding.ToEven);

        // Keep every detected model pixel on the fixed-size output canvas.
        offsetX = Math.Max(-bounds.Left, Math.Min(source.Width - bounds.Right, offsetX));
        offsetY = Math.Max(-bounds.Top, Math.Min(source.Height - bounds.Bottom, offsetY));

        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(source, offsetX, offsetY);
        }

        Write(
            log,
            "  projected foreground bounds=" + bounds.Left + "," + bounds.Top + "," + bounds.Width + "," + bounds.Height +
            " recenter_offset_px=" + offsetX + "," + offsetY);
        return output;
    }

    private static Rectangle FindProjectedForegroundBounds(Bitmap source)
    {
        var minX = source.Width;
        var minY = source.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (!IsProjectedForeground(source.GetPixel(x, y))) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < minX || maxY < minY
            ? Rectangle.Empty
            : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static bool IsProjectedForeground(Color color)
    {
        // The viewport background is forced to plain white. This threshold
        // retains faint translucent faces and antialiased hidden edges while
        // ignoring tiny near-white background variations.
        var distanceFromWhite = (255 - color.R) + (255 - color.G) + (255 - color.B);
        var colorSpread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        return distanceFromWhite >= 18 || colorSpread >= 8;
    }

    private static Bitmap CloneAsRgb(Bitmap source)
    {
        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(source, 0, 0);
        }
        return output;
    }

    private static Bitmap ScrubBlueAxisArtifacts(Bitmap source)
    {
        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var color = source.GetPixel(x, y);
                output.SetPixel(x, y, IsAxisBlue(color) ? AverageNearbyNonBlue(source, x, y) : color);
            }
        }
        return output;
    }

    private static bool IsAxisBlue(Color color)
    {
        var maxRedGreen = Math.Max(color.R, color.G);
        return color.B > 95 && color.B - maxRedGreen > 35 && color.R < 150 && color.G < 180;
    }

    private static Color AverageNearbyNonBlue(Bitmap source, int x, int y)
    {
        var radius = 4;
        var r = 0;
        var g = 0;
        var b = 0;
        var count = 0;
        for (var yy = Math.Max(0, y - radius); yy <= Math.Min(source.Height - 1, y + radius); yy++)
        {
            for (var xx = Math.Max(0, x - radius); xx <= Math.Min(source.Width - 1, x + radius); xx++)
            {
                var neighbor = source.GetPixel(xx, yy);
                if (IsAxisBlue(neighbor)) continue;
                r += neighbor.R;
                g += neighbor.G;
                b += neighbor.B;
                count++;
            }
        }

        if (count == 0) return Color.White;
        return Color.FromArgb(r / count, g / count, b / count);
    }

    private static int Luma(Color color)
    {
        return (int)(0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B);
    }

    private static Color Blend(Color bottom, Color top, double alpha)
    {
        if (alpha <= 0) return bottom;
        if (alpha >= 1) return top;
        var inv = 1.0 - alpha;
        return Color.FromArgb(
            Clamp(bottom.R * inv + top.R * alpha),
            Clamp(bottom.G * inv + top.G * alpha),
            Clamp(bottom.B * inv + top.B * alpha));
    }

    private static int Clamp(double value)
    {
        if (value < 0) return 0;
        if (value > 255) return 255;
        return (int)Math.Round(value);
    }

    private static string[] GetInputFiles(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.InputFile)) return new[] { options.InputFile };
        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.GetFiles(options.InputDir, "*.*", searchOption)
            .Where(IsRenderableInput)
            .Where(p => !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsRenderableInput(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".step" || ext == ".stp" || ext == ".sldprt" || ext == ".sldasm";
    }

    private static string GetInputKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".step" || ext == ".stp") return "step";
        if (ext == ".sldprt") return "sldprt";
        if (ext == ".sldasm") return "sldasm";
        return ext.TrimStart('.');
    }

    private static string RenderPngPath(string root, string stem, RenderStyle style)
    {
        return Path.Combine(root, RenderOutputDirectory, style.Key, stem + "__" + style.Key + ".png");
    }

    private static string RenderBmpPath(Options options, string temporaryBmpRoot, string stem, RenderStyle style)
    {
        var root = options.KeepBmp
            ? Path.Combine(options.OutputRoot, "bmp_raw")
            : temporaryBmpRoot;
        return Path.Combine(root, style.Key, stem + "__" + style.Key + ".bmp");
    }

    private static void EnsureFolders(string root, bool keepBmp)
    {
        Directory.CreateDirectory(Path.Combine(root, RenderOutputDirectory, BaseStyles[0].Key));
        Directory.CreateDirectory(Path.Combine(root, RenderOutputDirectory, BaseStyles[1].Key));
        Directory.CreateDirectory(Path.Combine(root, RenderOutputDirectory, CompositeStyle.Key));
        if (keepBmp)
        {
            Directory.CreateDirectory(Path.Combine(root, "bmp_raw", BaseStyles[0].Key));
            Directory.CreateDirectory(Path.Combine(root, "bmp_raw", BaseStyles[1].Key));
        }
        Directory.CreateDirectory(Path.Combine(root, "manifests"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Directory.Delete(path, true); } catch { }
    }

    private static void TryDeleteEmptyBmpRawDirectory(string outputRoot)
    {
        var path = Path.Combine(outputRoot, "bmp_raw");
        if (!Directory.Exists(path)) return;
        try
        {
            if (!Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(path, true);
            }
        }
        catch { }
    }

    private static List<int> ProcessIdsByName(string name)
    {
        var result = new List<int>();
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                result.Add(process.Id);
            }
        }
        catch { }
        return result;
    }

    private static void TryClose(SldWorks sw, ModelDoc2 model)
    {
        try
        {
            if (model != null) sw.CloseDoc(model.GetTitle());
        }
        catch { }
    }

    private static string ResultCsv(string status, string modelPath, string inputKind, string openMethod, RenderStyle style, string pngPath, string bmpPath, Options options, string message)
    {
        return string.Join(",", new[]
        {
            Csv(status),
            Csv(modelPath),
            Csv(inputKind),
            Csv(openMethod),
            Csv(style.Key),
            Csv(style.Description),
            Csv(pngPath),
            Csv(bmpPath),
            Csv(options.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Csv(options.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Csv(message)
        });
    }

    private static string Csv(string value)
    {
        if (value == null) value = "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string CleanMessage(string value)
    {
        if (value == null) return "";
        return value.Replace("\r", " ").Replace("\n", " ");
    }

    private static void Write(StreamWriter log, string message)
    {
        var line = DateTime.Now.ToString("s") + " " + message;
        log.WriteLine(line);
        log.Flush();
        Console.WriteLine(line);
    }

    private static Options ParseArgs(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i].ToLowerInvariant();
            if (key == "--help" || key == "-h" || key == "/?") options.ShowHelp = true;
            else if (key == "--input-dir" && i + 1 < args.Length) options.InputDir = args[++i];
            else if (key == "--input-file" && i + 1 < args.Length) options.InputFile = args[++i];
            else if (key == "--output-root" && i + 1 < args.Length) options.OutputRoot = args[++i];
            else if (key == "--width" && i + 1 < args.Length) options.Width = int.Parse(args[++i]);
            else if (key == "--height" && i + 1 < args.Length) options.Height = int.Parse(args[++i]);
            else if (key == "--max-processed" && i + 1 < args.Length) options.MaxProcessed = int.Parse(args[++i]);
            else if (key == "--recursive") options.Recursive = true;
            else if (key == "--keep-bmp") options.KeepBmp = true;
            else if (key == "--skip-existing") options.SkipExisting = true;
            else if (key == "--visible") options.Visible = true;
            else if (key == "--close-when-done") options.CloseWhenDone = true;
            else if (key == "--part-template" && i + 1 < args.Length) options.PartTemplate = args[++i];
            else if (key == "--assembly-template" && i + 1 < args.Length) options.AssemblyTemplate = args[++i];
            else throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
        }
        return options;
    }

    private static void ValidateOptions(Options options)
    {
        var hasInputFile = !string.IsNullOrWhiteSpace(options.InputFile);
        var hasInputDir = !string.IsNullOrWhiteSpace(options.InputDir);
        if (hasInputFile == hasInputDir)
        {
            throw new ArgumentException("Specify exactly one of --input-file or --input-dir.");
        }
        if (hasInputFile)
        {
            if (!File.Exists(options.InputFile)) throw new FileNotFoundException("Input model not found.", options.InputFile);
            if (!IsRenderableInput(options.InputFile)) throw new ArgumentException("--input-file must be .step, .stp, .sldprt, or .sldasm.");
            if (Path.GetFileName(options.InputFile).StartsWith("._", StringComparison.Ordinal))
            {
                throw new ArgumentException("AppleDouble ._* files are not valid inputs.");
            }
            options.InputFile = Path.GetFullPath(options.InputFile);
        }
        else
        {
            if (!Directory.Exists(options.InputDir)) throw new DirectoryNotFoundException("Input directory not found: " + options.InputDir);
            options.InputDir = Path.GetFullPath(options.InputDir);
        }

        if (options.Width <= 0) throw new ArgumentOutOfRangeException("--width", "Width must be positive.");
        if (options.Height <= 0) throw new ArgumentOutOfRangeException("--height", "Height must be positive.");
        if (options.MaxProcessed <= 0) throw new ArgumentOutOfRangeException("--max-processed", "Max processed must be positive.");
        options.OutputRoot = Path.GetFullPath(options.OutputRoot);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SOLIDWORKS transparent perspective 3D renderer");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  --input-file <model.step|model.sldprt>  or  --input-dir <folder>");
        Console.WriteLine("  --output-root <folder>");
        Console.WriteLine();
        Console.WriteLine("Optional:");
        Console.WriteLine("  --recursive --skip-existing --keep-bmp --close-when-done");
        Console.WriteLine("  --width <px> --height <px> --max-processed <n>");
        Console.WriteLine("  --part-template <file.prtdot> --assembly-template <file.asmdot>");
    }
}
