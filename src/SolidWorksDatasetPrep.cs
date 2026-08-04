using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static class SolidWorksDatasetPrep
{
    private sealed class Options
    {
        public string InputDir = "";
        public string InputFile = "";
        public string OutputRoot = Path.Combine(System.Environment.CurrentDirectory, "output");
        public string DrawingTemplate = "";
        public string PartTemplate = "";
        public string AssemblyTemplate = "";
        public string PdfToCairoExe = "pdftocairo.exe";
        public int MaxSuccess;
        public int MaxProcessed = int.MaxValue;
        public double TargetMaxDimension = 0.1;
        public double NormalizedStepTargetMaxDimension = 0.0018;
        public double DrawingScale = 1.0;
        public bool NormalizeOnly;
        public bool Visible;
        public bool CloseWhenDone;
        public bool Recursive;
        public bool SkipExistingNormalizedStep;
        public bool PreserveStepFileName;
        public int MaxFailuresPerStep = 0;
        public string NormalizedStepDir = "";
        public string CheckpointCsv = "";
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

    private sealed class NormalizeInfo
    {
        public double[] OriginalBox = new double[0];
        public double[] NormalizedBox = new double[0];
        public double[] Center = new double[0];
        public double Scale;
        public int BodyCount;
    }

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
            EnsureOutputFolders(options);
            var logPath = Path.Combine(options.OutputRoot, "logs", "dataset_prep_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
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

    private static void EnsureOutputFolders(Options options)
    {
        var root = options.OutputRoot;
        Directory.CreateDirectory(GetNormalizedStepDir(options));
        Directory.CreateDirectory(Path.Combine(root, "manifests"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        if (!string.IsNullOrWhiteSpace(options.CheckpointCsv))
        {
            var checkpointDir = Path.GetDirectoryName(options.CheckpointCsv);
            if (!string.IsNullOrWhiteSpace(checkpointDir)) Directory.CreateDirectory(checkpointDir);
        }

        if (!options.NormalizeOnly)
        {
            var techDrawRoot = Path.Combine(root, "techdraw");
            Directory.CreateDirectory(Path.Combine(techDrawRoot, "pdf"));
            Directory.CreateDirectory(Path.Combine(techDrawRoot, "dxf"));
            Directory.CreateDirectory(Path.Combine(techDrawRoot, "svg"));
            Directory.CreateDirectory(Path.Combine(root, "projection_maps"));
        }
    }

    private static int Run(Options options, StreamWriter log)
    {
        string[] stepFiles;
        if (!string.IsNullOrWhiteSpace(options.InputFile))
        {
            stepFiles = new[] { Path.GetFullPath(options.InputFile) };
        }
        else
        {
            var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            stepFiles = Directory.GetFiles(options.InputDir, "*.st*p", searchOption)
                .Where(p => !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (stepFiles.Length == 0)
        {
            Write(log, "No STEP files found in " + options.InputDir);
            return 1;
        }

        SldWorks sw = null;
        var preexistingSolidWorksProcessIds = new HashSet<int>(Process.GetProcessesByName("SLDWORKS").Select(process => process.Id));
        var solidWorksProcessId = 0;
        var ownsSolidWorksProcess = false;
        var rows = new List<string>();
        var header = "Status,StepPath,NormalizedStepPath,PdfPath,DxfPath,SvgPath,ProjectionMapPath,OpenMethod,ApproxOriginalPartBox,ApproxDrawingModelPartBox,DrawingModelScale,ApproxNormalizedStepModelPartBox,NormalizedStepScaleFromDrawingModel,NormalizedStepTotalScale,Message";
        rows.Add(header);
        EnsureCheckpointCsv(options, header + ",StartedAt,FinishedAt");
        var previousFailures = LoadFailureCounts(options);

        try
        {
            sw = new SldWorks();
            try
            {
                solidWorksProcessId = sw.GetProcessID();
                ownsSolidWorksProcess = solidWorksProcessId > 0 && !preexistingSolidWorksProcessIds.Contains(solidWorksProcessId);
            }
            catch { }
            sw.Visible = options.Visible;
            Write(log, "SOLIDWORKS revision: " + sw.RevisionNumber());
            Write(log, "DrawingModelTargetMaxDimension(m)=" + options.TargetMaxDimension.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Write(log, "NormalizedStepTargetMaxDimension(m)=" + options.NormalizedStepTargetMaxDimension.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " NormalizeOnly=" + options.NormalizeOnly);
            Write(log, "Projection=third_angle Sheet=A4_landscape DrawingScale=" + options.DrawingScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Write(log, "DrawingViewDisplayMode=HLV_hidden_lines_visible TangentEdges=hidden");
            Write(log, "PartTemplate=" + options.PartTemplate);
            Write(log, "AssemblyTemplate=" + options.AssemblyTemplate);
            if (!options.NormalizeOnly) Write(log, "DrawingTemplate=" + options.DrawingTemplate);
            SetTemplatePreferences(sw, options);

            var ok = 0;
            var skippedExisting = 0;
            var skippedMaxFailures = 0;
            var skippedUnsupported = 0;
            var failed = 0;
            var processed = 0;

            foreach (var stepPath in stepFiles)
            {
                if ((options.MaxSuccess > 0 && (ok + skippedExisting + skippedMaxFailures) >= options.MaxSuccess) || processed >= options.MaxProcessed)
                {
                    break;
                }
                processed++;

                var startedAt = DateTime.Now.ToString("s");
                Write(log, "Processing " + stepPath);
                var status = "failed";
                var message = "";
                var normalizedStepPath = "";
                var temporaryDrawingNativeDir = "";
                var temporaryDrawingNativePath = "";
                var pdfPath = "";
                var dxfPath = "";
                var svgPath = "";
                var projectionMapPath = "";
                OpenResult open = null;
                ModelDoc2 normalizedStepModel = null;
                NormalizeInfo normalize = null;
                NormalizeInfo normalizedStepNormalize = null;
                double? normalizedStepScaleFromDrawingModel = null;
                double? normalizedStepTotalScale = null;
                var stem = Path.GetFileNameWithoutExtension(stepPath);
                var normalizedStepFileName = options.PreserveStepFileName ? Path.GetFileName(stepPath) : stem + "__normalized.step";
                normalizedStepPath = Path.Combine(GetNormalizedStepDir(options), normalizedStepFileName);

                try
                {
                    if (options.SkipExistingNormalizedStep && File.Exists(normalizedStepPath))
                    {
                        status = "skipped_existing";
                        message = "Existing normalized STEP found; skipped.";
                        skippedExisting++;
                        Write(log, "SKIPPED existing " + normalizedStepPath);
                    }
                    else if (options.MaxFailuresPerStep > 0 && GetFailureCount(previousFailures, stepPath) >= options.MaxFailuresPerStep)
                    {
                        status = "skipped_max_failures";
                        message = "Previous failure count reached " + options.MaxFailuresPerStep + "; skipped.";
                        skippedMaxFailures++;
                        Write(log, "SKIPPED max failures " + stepPath);
                    }
                    else
                    {
                        open = OpenStep(sw, stepPath, log);
                        if (open.Model == null)
                        {
                            throw new InvalidOperationException("All open attempts failed: " + string.Join(" | ", open.Attempts.ToArray()));
                        }
                        if (open.Model.GetType() != (int)swDocumentTypes_e.swDocPART)
                        {
                            status = "skipped";
                            message = "Imported document is not a part; assembly normalization is not enabled in this tool.";
                            skippedUnsupported++;
                        }
                        else
                        {
                            if (!options.NormalizeOnly)
                            {
                                var techDrawRoot = Path.Combine(options.OutputRoot, "techdraw");
                                pdfPath = Path.Combine(techDrawRoot, "pdf", stem + "__normalized.pdf");
                                dxfPath = Path.Combine(techDrawRoot, "dxf", stem + "__normalized.dxf");
                                svgPath = Path.Combine(techDrawRoot, "svg", stem + "__normalized.svg");
                                projectionMapPath = Path.Combine(options.OutputRoot, "projection_maps", stem + "__normalized_projection_map.json");
                            }

                            normalize = NormalizePart(sw, open.Model, options.TargetMaxDimension);
                            if (!options.NormalizeOnly)
                            {
                                temporaryDrawingNativeDir = Path.Combine(Path.GetTempPath(), "SolidWorksDatasetPrep", Guid.NewGuid().ToString("N"));
                                Directory.CreateDirectory(temporaryDrawingNativeDir);
                                temporaryDrawingNativePath = Path.Combine(temporaryDrawingNativeDir, stem + "__drawing_100mm.SLDPRT");
                                SaveAs(open.Model, temporaryDrawingNativePath, "temporary drawing native");
                                CreateDrawing(sw, options.DrawingTemplate, temporaryDrawingNativePath, pdfPath, dxfPath, svgPath, projectionMapPath, options.DrawingScale, options.TargetMaxDimension, options.NormalizedStepTargetMaxDimension, options.PdfToCairoExe);
                                open.Model = null;
                            }

                            if (NearlyEqual(options.NormalizedStepTargetMaxDimension, options.TargetMaxDimension))
                            {
                                normalizedStepModel = options.NormalizeOnly ? open.Model : OpenNativePart(sw, temporaryDrawingNativePath);
                                normalizedStepNormalize = normalize;
                                normalizedStepScaleFromDrawingModel = 1.0;
                                normalizedStepTotalScale = normalize.Scale;
                            }
                            else
                            {
                                if (string.IsNullOrWhiteSpace(temporaryDrawingNativeDir))
                                {
                                    temporaryDrawingNativeDir = Path.Combine(Path.GetTempPath(), "SolidWorksDatasetPrep", Guid.NewGuid().ToString("N"));
                                    Directory.CreateDirectory(temporaryDrawingNativeDir);
                                }
                                var temporaryDrawingStepPath = Path.Combine(temporaryDrawingNativeDir, stem + "__drawing_scale.step");
                                normalizedStepModel = options.NormalizeOnly ? open.Model : OpenNativePart(sw, temporaryDrawingNativePath);
                                SaveAs(normalizedStepModel, temporaryDrawingStepPath, "temporary drawing-scale STEP");
                                TryClose(sw, normalizedStepModel);
                                normalizedStepModel = null;
                                open.Model = null;

                                var normalizedStepOpen = OpenStep(sw, temporaryDrawingStepPath, log);
                                if (normalizedStepOpen.Model == null)
                                {
                                    throw new InvalidOperationException("Could not reopen temporary drawing-scale STEP: " + string.Join(" | ", normalizedStepOpen.Attempts.ToArray()));
                                }
                                if (normalizedStepOpen.Model.GetType() != (int)swDocumentTypes_e.swDocPART)
                                {
                                    TryClose(sw, normalizedStepOpen.Model);
                                    throw new InvalidOperationException("Temporary drawing-scale STEP did not reopen as a part.");
                                }
                                normalizedStepModel = normalizedStepOpen.Model;
                                normalizedStepNormalize = NormalizePart(sw, normalizedStepModel, options.NormalizedStepTargetMaxDimension);
                                normalizedStepScaleFromDrawingModel = normalizedStepNormalize.Scale;
                                normalizedStepTotalScale = normalize.Scale * normalizedStepNormalize.Scale;
                            }
                            SaveAs(normalizedStepModel, normalizedStepPath, "normalized STEP");
                            TryClose(sw, normalizedStepModel);
                            normalizedStepModel = null;
                            open.Model = null;

                            status = "ok";
                            message = options.NormalizeOnly
                                ? "Exported normalized STEP only."
                                : "Rendered PDF/DXF/SVG from a temporary drawing model and exported normalized STEP.";
                            ok++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    message = ex.Message.Replace("\r", " ").Replace("\n", " ");
                    Write(log, "FAILED " + message);
                    IncrementFailureCount(previousFailures, stepPath);
                    failed++;
                }
                finally
                {
                    if (open != null && open.Model != null)
                    {
                        TryClose(sw, open.Model);
                    }
                    if (normalizedStepModel != null)
                    {
                        TryClose(sw, normalizedStepModel);
                    }
                    TryDeleteTemporaryDirectory(temporaryDrawingNativeDir);
                }

                var resultValues = new List<string>(new[]
                {
                    Csv(status),
                    Csv(stepPath),
                    Csv(normalizedStepPath),
                    Csv(pdfPath),
                    Csv(dxfPath),
                    Csv(svgPath),
                    Csv(projectionMapPath),
                    Csv(open == null ? "" : open.Method),
                    Csv(normalize == null ? "" : JsonArray(normalize.OriginalBox)),
                    Csv(normalize == null ? "" : JsonArray(normalize.NormalizedBox)),
                    Csv(normalize == null ? "" : normalize.Scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                    Csv(normalizedStepNormalize == null ? "" : JsonArray(normalizedStepNormalize.NormalizedBox)),
                    Csv(normalizedStepScaleFromDrawingModel.HasValue ? normalizedStepScaleFromDrawingModel.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : ""),
                    Csv(normalizedStepTotalScale.HasValue ? normalizedStepTotalScale.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : ""),
                    Csv(message)
                });
                rows.Add(string.Join(",", resultValues.ToArray()));
                AppendCheckpointRow(options, resultValues, startedAt, DateTime.Now.ToString("s"));
            }

            var csvPath = Path.Combine(options.OutputRoot, "manifests", "dataset_prep_results_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            File.WriteAllLines(csvPath, rows.ToArray());
            Write(log, "Done. OK=" + ok + " SkippedExisting=" + skippedExisting + " SkippedMaxFailures=" + skippedMaxFailures + " SkippedUnsupported=" + skippedUnsupported + " Failed=" + failed + " Processed=" + processed + " Results=" + csvPath);
            return failed == 0 && skippedUnsupported == 0 && skippedMaxFailures == 0 ? 0 : 1;
        }
        finally
        {
            if (sw != null && options.CloseWhenDone)
            {
                try { sw.CloseAllDocuments(true); } catch { }
                try { sw.ExitApp(); } catch { }
                if (ownsSolidWorksProcess && solidWorksProcessId > 0)
                {
                    try
                    {
                        using (var process = Process.GetProcessById(solidWorksProcessId))
                        {
                            if (!process.WaitForExit(5000)) process.Kill();
                        }
                    }
                    catch { }
                }
            }
        }
    }

    private static string GetNormalizedStepDir(Options options)
    {
        return string.IsNullOrWhiteSpace(options.NormalizedStepDir)
            ? Path.Combine(options.OutputRoot, "normalized_step")
            : options.NormalizedStepDir;
    }

    private static void EnsureCheckpointCsv(Options options, string header)
    {
        if (string.IsNullOrWhiteSpace(options.CheckpointCsv) || File.Exists(options.CheckpointCsv)) return;
        File.WriteAllText(options.CheckpointCsv, header + System.Environment.NewLine);
    }

    private static void AppendCheckpointRow(Options options, List<string> resultValues, string startedAt, string finishedAt)
    {
        if (string.IsNullOrWhiteSpace(options.CheckpointCsv)) return;
        var checkpointValues = new List<string>(resultValues);
        checkpointValues.Add(Csv(startedAt));
        checkpointValues.Add(Csv(finishedAt));
        File.AppendAllText(options.CheckpointCsv, string.Join(",", checkpointValues.ToArray()) + System.Environment.NewLine);
    }

    private static Dictionary<string, int> LoadFailureCounts(Options options)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(options.CheckpointCsv) || !File.Exists(options.CheckpointCsv)) return counts;

        foreach (var line in File.ReadLines(options.CheckpointCsv).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            if (fields.Count < 2) continue;
            if (string.Equals(fields[0], "failed", StringComparison.OrdinalIgnoreCase))
            {
                IncrementFailureCount(counts, fields[1]);
            }
        }
        return counts;
    }

    private static int GetFailureCount(Dictionary<string, int> counts, string stepPath)
    {
        int count;
        return counts.TryGetValue(stepPath, out count) ? count : 0;
    }

    private static void IncrementFailureCount(Dictionary<string, int> counts, string stepPath)
    {
        if (string.IsNullOrWhiteSpace(stepPath)) return;
        counts[stepPath] = GetFailureCount(counts, stepPath) + 1;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Length = 0;
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static OpenResult OpenStep(SldWorks sw, string path, StreamWriter log)
    {
        var result = new OpenResult();
        TrySetImportPreferences(sw);
        TryOpenDoc7(sw, path, (int)swDocumentTypes_e.swDocPART, "OpenDoc7_part", result, log);
        if (result.Model != null) return result;
        TryOpenDoc7(sw, path, (int)swDocumentTypes_e.swDocASSEMBLY, "OpenDoc7_assembly", result, log);
        if (result.Model != null) return result;
        TryOpenDoc6(sw, path, (int)swDocumentTypes_e.swDocPART, "OpenDoc6_part_silent", result, log);
        if (result.Model != null) return result;
        TryLoadFile4(sw, path, "r", "LoadFile4_r", result, log);
        return result;
    }

    private static void TryOpenDoc7(SldWorks sw, string path, int docType, string name, OpenResult result, StreamWriter log)
    {
        try
        {
            var spec = (DocumentSpecification)sw.GetOpenDocSpec(path);
            spec.FileName = path;
            spec.DocumentType = docType;
            spec.Silent = true;
            spec.ReadOnly = false;
            spec.ViewOnly = false;
            spec.LoadModel = true;
            spec.AutoRepair = true;
            spec.CriticalDataRepair = true;
            var model = sw.OpenDoc7(spec);
            result.Errors = spec.Error;
            result.Warnings = spec.Warning;
            result.Attempts.Add(name + ": errors=" + result.Errors + " warnings=" + result.Warnings + " opened=" + (model != null));
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

    private static void TryOpenDoc6(SldWorks sw, string path, int docType, string name, OpenResult result, StreamWriter log)
    {
        try
        {
            var errors = 0;
            var warnings = 0;
            var model = sw.OpenDoc6(path, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
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

    private static NormalizeInfo NormalizePart(SldWorks sw, ModelDoc2 model, double targetMaxDimension)
    {
        model.ForceRebuild3(false);
        var part = (PartDoc)model;
        var originalBox = ToDoubleArray(part.GetPartBox(true));
        if (originalBox.Length < 6)
        {
            throw new InvalidOperationException("Could not read part bounding box.");
        }

        var dx = originalBox[3] - originalBox[0];
        var dy = originalBox[4] - originalBox[1];
        var dz = originalBox[5] - originalBox[2];
        var maxDim = Math.Max(dx, Math.Max(dy, dz));
        if (maxDim <= 0)
        {
            throw new InvalidOperationException("Degenerate part bounding box.");
        }

        var center = new[]
        {
            (originalBox[0] + originalBox[3]) / 2.0,
            (originalBox[1] + originalBox[4]) / 2.0,
            (originalBox[2] + originalBox[5]) / 2.0
        };
        var scale = targetMaxDimension / maxDim;

        var math = (MathUtility)sw.GetMathUtility();
        var data = new double[]
        {
            1, 0, 0,
            0, 1, 0,
            0, 0, 1,
            -center[0] * scale, -center[1] * scale, -center[2] * scale,
            scale, 0, 0, 0
        };
        var transform = (MathTransform)math.CreateTransform(data);
        if (transform == null)
        {
            throw new InvalidOperationException("Could not create normalization transform.");
        }

        var bodies = GetBodies(part);
        if (bodies.Count == 0)
        {
            throw new InvalidOperationException("No bodies found to normalize.");
        }
        foreach (var body in bodies)
        {
            if (!body.ApplyTransform(transform))
            {
                throw new InvalidOperationException("Body transform failed.");
            }
        }

        model.ForceRebuild3(false);
        var normalizedBox = ToDoubleArray(part.GetPartBox(true));
        return new NormalizeInfo
        {
            OriginalBox = originalBox,
            NormalizedBox = normalizedBox,
            Center = center,
            Scale = scale,
            BodyCount = bodies.Count
        };
    }

    private static List<Body2> GetBodies(PartDoc part)
    {
        var bodies = new List<Body2>();
        AddBodies(part.GetBodies2((int)swBodyType_e.swSolidBody, true), bodies);
        AddBodies(part.GetBodies2((int)swBodyType_e.swSheetBody, true), bodies);
        return bodies;
    }

    private static void AddBodies(object value, List<Body2> bodies)
    {
        if (value == null) return;
        var array = value as object[];
        if (array != null)
        {
            foreach (var item in array)
            {
                var body = item as Body2;
                if (body != null) bodies.Add(body);
            }
            return;
        }
        var single = value as Body2;
        if (single != null) bodies.Add(single);
    }

    private static bool NearlyEqual(double left, double right)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }

    private static ModelDoc2 OpenNativePart(SldWorks sw, string path)
    {
        var errors = 0;
        var warnings = 0;
        var model = (ModelDoc2)sw.OpenDoc6(
            path,
            (int)swDocumentTypes_e.swDocPART,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            "",
            ref errors,
            ref warnings);
        if (model == null)
        {
            throw new InvalidOperationException("Could not reopen temporary drawing model. errors=" + errors + " warnings=" + warnings);
        }
        return model;
    }

    private static void CreateDrawing(SldWorks sw, string template, string nativePath, string pdfPath, string dxfPath, string svgPath, string projectionMapPath, double drawingScale, double targetMaxDimension, double normalizedStepTargetMaxDimension, string pdfToCairoExe)
    {
        var drawingModel = (ModelDoc2)sw.NewDocument(template, (int)swDwgPaperSizes_e.swDwgPaperA4size, 0, 0);
        if (drawingModel == null)
        {
            throw new InvalidOperationException("New drawing failed.");
        }

        try
        {
            var drawing = (DrawingDoc)drawingModel;
            ApplyBlankA4Sheet(drawing);
            if (!drawing.Create3rdAngleViews2(nativePath))
            {
                throw new InvalidOperationException("Create3rdAngleViews2 failed.");
            }
            drawingModel.ForceRebuild3(false);
            ApplyViewScaleAndLayout(drawing, drawingScale);
            drawingModel.ForceRebuild3(false);
            ApplyDisplayModeToModelViews(drawing, (int)swDisplayMode_e.swHIDDEN_GREYED, false);
            drawingModel.ForceRebuild3(false);
            NormalizeDrawingLayerNames(drawingModel);
            drawingModel.ForceRebuild3(false);
            WriteProjectionMap(drawing, pdfPath, projectionMapPath, drawingScale, targetMaxDimension, normalizedStepTargetMaxDimension);
            var temporaryDrawingPath = Path.Combine(Path.GetDirectoryName(nativePath), Path.GetFileNameWithoutExtension(nativePath) + "__views.SLDDRW");
            SaveAs(drawingModel, temporaryDrawingPath, "temporary drawing");
            SaveAs(drawingModel, pdfPath, "pdf");
            SaveAs(drawingModel, dxfPath, "dxf");
            if (!TrySaveAs(drawingModel, svgPath))
            {
                ConvertPdfToSvg(pdfPath, svgPath, pdfToCairoExe);
            }
        }
        finally
        {
            TryClose(sw, drawingModel);
            TryCloseByPath(sw, nativePath);
        }
    }

    private static void ApplyViewScaleAndLayout(DrawingDoc drawing, double drawingScale)
    {
        var positions = new[]
        {
            new[] { 0.083, 0.057 },
            new[] { 0.083, 0.158 },
            new[] { 0.223, 0.057 }
        };

        var viewIndex = 0;
        View view = null;
        try { view = (View)drawing.GetFirstView(); } catch { }
        while (view != null)
        {
            var next = GetNextView(view);
            var referencedModel = "";
            try { referencedModel = view.GetReferencedModelName(); } catch { }
            if (!string.IsNullOrWhiteSpace(referencedModel))
            {
                try { view.UseParentScale = false; } catch { }
                try { view.UseSheetScale = 0; } catch { }
                try { view.ScaleDecimal = drawingScale; } catch { }
                if (viewIndex < positions.Length)
                {
                    try { view.Position = positions[viewIndex]; } catch { }
                }
                try { view.UpdateViewDisplayGeometry(); } catch { }
                viewIndex++;
            }
            view = next;
        }
    }

    private static void ApplyBlankA4Sheet(DrawingDoc drawing)
    {
        object namesObj = drawing.GetSheetNames();
        var sheetNames = namesObj as string[];
        var sheetName = sheetNames != null && sheetNames.Length > 0 ? sheetNames[0] : "Sheet1";

        drawing.SetupSheet6(
            sheetName,
            (int)swDwgPaperSizes_e.swDwgPaperA4size,
            (int)swDwgTemplates_e.swDwgTemplateNone,
            1.0,
            1.0,
            false,
            "",
            0.297,
            0.210,
            "",
            true,
            0.0,
            0.0,
            0.0,
            0.0,
            0,
            0);
    }

    private static void ApplyDisplayModeToModelViews(DrawingDoc drawing, int displayMode, bool edges)
    {
        View view = null;
        try { view = (View)drawing.GetFirstView(); } catch { }
        while (view != null)
        {
            var next = GetNextView(view);
            var referencedModel = "";
            try { referencedModel = view.GetReferencedModelName(); } catch { }
            if (!string.IsNullOrWhiteSpace(referencedModel))
            {
                var applied = false;
                try { applied = view.SetDisplayMode4(false, displayMode, false, edges, true); } catch { }
                if (!applied)
                {
                    try { view.SetDisplayMode3(false, displayMode, false, edges); } catch { }
                }
                try { view.SetDisplayTangentEdges2((int)swDisplayTangentEdges_e.swTangentEdgesHidden); } catch { }
                try { view.UpdateViewDisplayGeometry(); } catch { }
            }
            view = next;
        }
    }

    private sealed class DrawingLayerInfo
    {
        public Layer Layer;
        public int Id;
        public string OriginalName;
    }

    private static void NormalizeDrawingLayerNames(ModelDoc2 drawingModel)
    {
        var manager = (LayerMgr)drawingModel.GetLayerManager();
        if (manager == null)
        {
            throw new InvalidOperationException("Could not access drawing layer manager.");
        }

        var names = ToStringArray(manager.GetLayerList());
        var layers = new List<DrawingLayerInfo>();
        foreach (var name in names)
        {
            if (string.Equals(name, "0", StringComparison.Ordinal)) continue;
            var layer = manager.GetLayer(name) as Layer;
            if (layer == null)
            {
                throw new InvalidOperationException("Could not access drawing layer: " + name);
            }
            layers.Add(new DrawingLayerInfo { Layer = layer, Id = layer.GetID(), OriginalName = name });
        }
        layers.Sort((left, right) => left.Id.CompareTo(right.Id));

        var temporaryPrefix = "__SWDP_LAYER_" + Guid.NewGuid().ToString("N") + "_";
        for (var i = 0; i < layers.Count; i++)
        {
            layers[i].Layer.Name = temporaryPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        for (var i = 0; i < layers.Count; i++)
        {
            var targetName = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            layers[i].Layer.Name = targetName;
            if (!string.Equals(layers[i].Layer.Name, targetName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Could not rename drawing layer " + layers[i].OriginalName + " to " + targetName + ".");
            }
        }
    }

    private static void WriteProjectionMap(DrawingDoc drawing, string pdfPath, string projectionMapPath, double drawingScale, double targetMaxDimension, double normalizedStepTargetMaxDimension)
    {
        using (var writer = new StreamWriter(projectionMapPath))
        {
            writer.WriteLine("{");
            writer.WriteLine("  \"generator\": \"SolidWorksDatasetPrep\",");
            writer.WriteLine("  \"projection\": \"third_angle\",");
            writer.WriteLine("  \"drawing_display_mode\": 1,");
            writer.WriteLine("  \"drawing_view_display_mode_name\": \"HLV_hidden_lines_visible\",");
            writer.WriteLine("  \"tangent_edges\": \"hidden\",");
            writer.WriteLine("  \"drawing_layer_name_policy\": \"preserve_0_then_numeric_by_layer_id\",");
            writer.WriteLine("  \"sheet\": {\"size\":\"A4\",\"orientation\":\"landscape\",\"width_m\":0.297,\"height_m\":0.210},");
            writer.WriteLine("  \"requested_drawing_scale\": " + drawingScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",");
            writer.WriteLine("  \"drawing_model_source\": \"temporary_solidworks_part\",");
            writer.WriteLine("  \"drawing_pdf\": \"" + Json(pdfPath) + "\",");
            writer.WriteLine("  \"units\": \"meters\",");
            writer.WriteLine("  \"normalization\": {\"preserve_axes\":true,\"center_to_origin\":true,\"drawing_model_target_max_dimension_m\":" + targetMaxDimension.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"normalized_step_target_max_dimension_m\":" + normalizedStepTargetMaxDimension.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "},");
            writer.WriteLine("  \"views\": [");

            var first = true;
            var viewIndex = 0;
            View view = null;
            try { view = (View)drawing.GetFirstView(); } catch { }
            while (view != null)
            {
                var referencedModel = "";
                try { referencedModel = view.GetReferencedModelName(); } catch { }
                if (!string.IsNullOrWhiteSpace(referencedModel))
                {
                    if (!first) writer.WriteLine(",");
                    first = false;
                    writer.Write("    {");
                    writer.Write("\"view_index\":" + viewIndex);
                    writer.Write(",\"third_angle_role\":\"" + ThirdAngleRole(viewIndex) + "\"");
                    writer.Write(",\"display_mode\":" + SafeGetDisplayMode(view));
                    writer.Write(",\"position_m\":" + JsonArray(ToDoubleArray(SafeObject(() => view.Position))));
                    writer.Write(",\"outline_m\":" + JsonArray(ToDoubleArray(SafeObject(() => view.GetOutline()))));
                    writer.Write(",\"scale_decimal\":" + SafeDouble(() => view.ScaleDecimal).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    writer.Write(",\"model_to_view_transform\":" + JsonArray(GetMathTransformArray(view)));
                    writer.Write("}");
                    viewIndex++;
                }
                view = GetNextView(view);
            }
            writer.WriteLine();
            writer.WriteLine("  ]");
            writer.WriteLine("}");
        }
    }

    private static void SaveAs(ModelDoc2 model, string path, string label)
    {
        var errors = 0;
        var warnings = 0;
        var ok = model.Extension.SaveAs(
            path,
            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
            null,
            ref errors,
            ref warnings);
        if (!ok || !File.Exists(path))
        {
            throw new InvalidOperationException(label + " save failed. errors=" + errors + " warnings=" + warnings);
        }
    }

    private static bool TrySaveAs(ModelDoc2 model, string path)
    {
        try
        {
            var errors = 0;
            var warnings = 0;
            return model.Extension.SaveAs(
                path,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                ref errors,
                ref warnings) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static void ConvertPdfToSvg(string pdfPath, string svgPath, string pdfToCairoExe)
    {
        var start = new ProcessStartInfo
        {
            FileName = pdfToCairoExe,
            Arguments = "-svg \"" + pdfPath + "\" \"" + svgPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using (var process = Process.Start(start))
        {
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(svgPath))
            {
                throw new InvalidOperationException("SVG export failed. " + process.StandardError.ReadToEnd());
            }
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
        if (File.Exists(options.DrawingTemplate))
        {
            try { sw.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing, options.DrawingTemplate); } catch { }
        }
    }

    private static View GetNextView(View view)
    {
        try { return (View)view.GetNextView(); }
        catch { return null; }
    }

    private static string ThirdAngleRole(int index)
    {
        if (index == 0) return "front";
        if (index == 1) return "top";
        if (index == 2) return "right";
        return "extra";
    }

    private static object SafeObject(Func<object> fn)
    {
        try { return fn(); }
        catch { return null; }
    }

    private static int SafeGetDisplayMode(View view)
    {
        try { return view.GetDisplayMode2(); }
        catch { }
        try { return view.GetDisplayMode(); }
        catch { return -1; }
    }

    private static double SafeDouble(Func<double> fn)
    {
        try { return fn(); }
        catch { return 0.0; }
    }

    private static double[] GetMathTransformArray(View view)
    {
        try
        {
            var transform = view.ModelToViewTransform;
            if (transform != null)
            {
                return ToDoubleArray(transform.ArrayData);
            }
        }
        catch { }
        return new double[0];
    }

    private static double[] ToDoubleArray(object value)
    {
        if (value == null) return new double[0];
        var doubles = value as double[];
        if (doubles != null) return doubles;
        var objects = value as object[];
        if (objects != null)
        {
            var result = new List<double>();
            foreach (var item in objects)
            {
                try { result.Add(Convert.ToDouble(item)); } catch { }
            }
            return result.ToArray();
        }
        var enumerable = value as System.Collections.IEnumerable;
        if (enumerable != null && !(value is string))
        {
            var result = new List<double>();
            foreach (var item in enumerable)
            {
                try { result.Add(Convert.ToDouble(item)); } catch { }
            }
            return result.ToArray();
        }
        return new double[0];
    }

    private static string[] ToStringArray(object value)
    {
        if (value == null) return new string[0];
        var strings = value as string[];
        if (strings != null) return strings;
        var enumerable = value as System.Collections.IEnumerable;
        if (enumerable != null && !(value is string))
        {
            var result = new List<string>();
            foreach (var item in enumerable)
            {
                if (item != null) result.Add(Convert.ToString(item));
            }
            return result.ToArray();
        }
        return new string[0];
    }

    private static string JsonArray(double[] values)
    {
        if (values == null || values.Length == 0) return "[]";
        return "[" + string.Join(",", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + "]";
    }

    private static string Json(string value)
    {
        if (value == null) value = "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static void TryClose(SldWorks sw, ModelDoc2 model)
    {
        try
        {
            if (model != null) sw.CloseDoc(model.GetTitle());
        }
        catch { }
    }

    private static void TryCloseByPath(SldWorks sw, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { sw.CloseDoc(Path.GetFileName(path)); } catch { }
        try { sw.CloseDoc(Path.GetFileNameWithoutExtension(path)); } catch { }
    }

    private static void TryDeleteTemporaryDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SolidWorksDatasetPrep")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullDirectory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, true);
        }
        catch { }
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
            else if (key == "--max-success" && i + 1 < args.Length) options.MaxSuccess = int.Parse(args[++i]);
            else if (key == "--max-processed" && i + 1 < args.Length) options.MaxProcessed = int.Parse(args[++i]);
            else if (key == "--target-max-dimension" && i + 1 < args.Length) options.TargetMaxDimension = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (key == "--drawing-model-target-max-dimension" && i + 1 < args.Length) options.TargetMaxDimension = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (key == "--normalized-step-target-max-dimension" && i + 1 < args.Length) options.NormalizedStepTargetMaxDimension = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (key == "--drawing-scale" && i + 1 < args.Length) options.DrawingScale = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (key == "--normalize-only") options.NormalizeOnly = true;
            else if (key == "--visible") options.Visible = true;
            else if (key == "--close-when-done") options.CloseWhenDone = true;
            else if (key == "--recursive") options.Recursive = true;
            else if (key == "--skip-existing-normalized-step") options.SkipExistingNormalizedStep = true;
            else if (key == "--preserve-step-file-name") options.PreserveStepFileName = true;
            else if (key == "--max-failures-per-step" && i + 1 < args.Length) options.MaxFailuresPerStep = int.Parse(args[++i]);
            else if (key == "--normalized-step-dir" && i + 1 < args.Length) options.NormalizedStepDir = args[++i];
            else if (key == "--checkpoint-csv" && i + 1 < args.Length) options.CheckpointCsv = args[++i];
            else if (key == "--drawing-template" && i + 1 < args.Length) options.DrawingTemplate = args[++i];
            else if (key == "--part-template" && i + 1 < args.Length) options.PartTemplate = args[++i];
            else if (key == "--assembly-template" && i + 1 < args.Length) options.AssemblyTemplate = args[++i];
            else if (key == "--pdftocairo" && i + 1 < args.Length) options.PdfToCairoExe = args[++i];
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
            if (!File.Exists(options.InputFile)) throw new FileNotFoundException("Input STEP not found.", options.InputFile);
            var extension = Path.GetExtension(options.InputFile);
            if (!string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("--input-file must be a .step or .stp file.");
            }
            if (Path.GetFileName(options.InputFile).StartsWith("._", StringComparison.Ordinal))
            {
                throw new ArgumentException("AppleDouble ._*.step files are not valid STEP inputs.");
            }
        }
        else if (!Directory.Exists(options.InputDir))
        {
            throw new DirectoryNotFoundException("Input directory not found: " + options.InputDir);
        }

        if (options.TargetMaxDimension <= 0) throw new ArgumentOutOfRangeException("--drawing-model-target-max-dimension", "Drawing-model target dimension must be positive.");
        if (options.NormalizedStepTargetMaxDimension <= 0) throw new ArgumentOutOfRangeException("--normalized-step-target-max-dimension", "Normalized STEP target dimension must be positive.");
        if (options.DrawingScale <= 0) throw new ArgumentOutOfRangeException("--drawing-scale", "Drawing scale must be positive.");
        if (options.MaxSuccess < 0) throw new ArgumentOutOfRangeException("--max-success", "Max success cannot be negative; zero means unlimited.");
        if (options.MaxProcessed <= 0) throw new ArgumentOutOfRangeException("--max-processed", "Max processed must be positive.");

        RequireFile(options.PartTemplate, "part template", ".prtdot");
        RequireFile(options.AssemblyTemplate, "assembly template", ".asmdot");
        if (!options.NormalizeOnly) RequireFile(options.DrawingTemplate, "drawing template", ".drwdot");

        options.OutputRoot = Path.GetFullPath(options.OutputRoot);
        if (hasInputFile) options.InputFile = Path.GetFullPath(options.InputFile);
        if (hasInputDir) options.InputDir = Path.GetFullPath(options.InputDir);
    }

    private static void RequireFile(string path, string label, string expectedExtension)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Missing " + label + " path.");
        if (!File.Exists(path)) throw new FileNotFoundException("Configured " + label + " not found.", path);
        if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Configured " + label + " must use " + expectedExtension + ": " + path);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SOLIDWORKS STEP-to-drawing dataset preparation tool");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  --input-file <model.step>  or  --input-dir <folder>");
        Console.WriteLine("  --output-root <folder>");
        Console.WriteLine("  --part-template <file.prtdot>");
        Console.WriteLine("  --assembly-template <file.asmdot>");
        Console.WriteLine("  --drawing-template <file.drwdot>");
        Console.WriteLine();
        Console.WriteLine("Challenge defaults:");
        Console.WriteLine("  --drawing-model-target-max-dimension 0.1");
        Console.WriteLine("  --normalized-step-target-max-dimension 0.0018");
        Console.WriteLine("  --drawing-scale 1.0");
        Console.WriteLine("  A4 landscape, third-angle views, HLV (hidden lines visible), tangent edges hidden");
        Console.WriteLine();
        Console.WriteLine("Optional:");
        Console.WriteLine("  --recursive --visible --close-when-done");
        Console.WriteLine("  --max-success <n> (zero means unlimited) --max-processed <n>");
        Console.WriteLine("  --pdftocairo <path> --normalize-only");
        Console.WriteLine("  --skip-existing-normalized-step --checkpoint-csv <path>");
    }

    private static string Csv(string value)
    {
        if (value == null) value = "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static void Write(StreamWriter log, string text)
    {
        Console.WriteLine(text);
        log.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + text);
        log.Flush();
    }
}
