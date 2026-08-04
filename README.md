# ECCV 2026 CAD Workshop Challenge Dataset Preparation

This repository contains the dataset preparation code used for the ECCV CAD
Workshop Challenge. It imports STEP files with SOLIDWORKS and exports:

- normalized STEP files
- PDF drawings
- DXF drawings
- SVG drawings
- transparent 3D PNG renders
- run manifests and logs

Exported normalized STEP has a longest axis of `1.8 mm`. No rotation is applied.

## Tested Environment

This release was tested locally with:

- Windows 11, 64-bit
- Windows PowerShell 5.1
- SOLIDWORKS 2025, API revision `33.1.1`
- .NET Framework C# compiler `4.8.9232.0`
- Poppler `pdftocairo` `25.02.0`

The local SOLIDWORKS document templates used during testing were:

```text
part import:          C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\templates\gb_part.prtdot
assembly import:      C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\templates\gb_assembly.asmdot
drawing seed:         C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\templates\gb_a4.drwdot
final drawing sheet:  blank A4 landscape, set explicitly by the script
```

The script replaces the seed template's sheet with a blank A4 landscape sheet
before creating the views. Template-defined DXF layers, line types, and line
weights can still affect the exported DXF.

## Quick Start

Before running the scripts, install SOLIDWORKS and the .NET Framework C# compiler. pdftocairo is optional and is used as a fallback for SVG export.

Run commands from the repository root in PowerShell.

Check the local environment:

```powershell
.\scripts\verify_environment.ps1
```

Build the executable:

```powershell
.\scripts\build.ps1
```

`render.ps1` also builds the executable automatically if it is missing.
The generated `bin\` directory is local and should not be redistributed.

Run the five bundled example STEP files:

```powershell
.\scripts\render.ps1 `
  -InputDir .\examples\test_inputs `
  -OutputRoot .\examples\test_outputs
```

This reads `000000.step` through `000004.step` from `examples\test_inputs` and
writes the normalized STEP files, drawings, transparent perspective 3D PNGs,
manifests, and logs under `examples\test_outputs`.

Print the resolved example configuration without starting SOLIDWORKS:

```powershell
.\scripts\render.ps1 `
  -InputDir .\examples\test_inputs `
  -OutputRoot .\examples\test_outputs `
  -DryRun
```

For your own dataset, replace the two paths:

```powershell
.\scripts\render.ps1 `
  -InputDir 'C:\cad\step_inputs' `
  -OutputRoot 'C:\cad\drawing_outputs'
```

Use `-InputFile` instead of `-InputDir` to process one STEP file. Use
`-Recursive` for subdirectories, `-MaxProcessed 5` to limit a test run,
`-Visible` to show the SOLIDWORKS window, and `-Skip3D` to suppress PNG
rendering.

## Input Directory

`-InputDir` accepts a directory containing `.step` and `.stp` files. By default,
only files directly inside that directory are processed.

- Use `-Recursive` for nested directories.
- Files named `._*.step` or `._*.stp` are ignored.
- ZIP files are not extracted.
- Keep `OutputRoot` separate from `InputDir`.
- With `-Recursive`, input files must have unique filename stems.
- STEP files imported by SOLIDWORKS as assemblies are skipped.

## Output Layout

```text
output_root/
  normalized_step/       STEP files normalized to a 1.8 mm longest axis
  techdraw/
    dxf/                  Drawing DXFs
    pdf/                  Drawing PDFs
    svg/                  Drawing SVGs
  render_3D/
    transparent_shaded_edges_perspective/
    hlg_perspective/
    hlg_translucent_faces_perspective/
  projection_maps/        Drawing-view metadata
  manifests/              CSV results
  logs/                   Processing logs
```

Imported and normalized SOLIDWORKS parts, intermediate STEP files, and
`.SLDDRW` files exist only in memory or temporary storage and are deleted after
export.

## Drawing Configuration

- blank A4 landscape sheet
- third-angle front, top, and right views
- drawing views use Hidden Lines Visible (`HLV`; SOLIDWORKS enum
  `swHIDDEN_GREYED`)
- tangent edges hidden
- DXF layer `0` is preserved; all other drawing layers are renamed to
  `1`, `2`, `3`, and so on in stable layer-ID order

The exported edge set is the direct result of the configured SOLIDWORKS
engineering-drawing projection. This release applies no additional B-rep edge
filter or DXF post-processing, so the PDF, DXF, and SVG consistently preserve
that projection result.

See [Method](docs/METHOD.md) for the normalization and drawing settings, and
[Known Differences](docs/KNOWN_DIFFERENCES.md) for general limitations.

## License

The source code is released under the MIT License. SOLIDWORKS, its API interop
assemblies, and its document templates remain subject to their vendor licenses.
