# Method

## Import

The tool imports `.step` and `.stp` files with SOLIDWORKS. Only `part` documents
are processed.

## Normalization

```text
center = (min + max) / 2
max_dimension = max(x_size, y_size, z_size)
scale = target_max_dimension / max_dimension
p_normalized = scale * (p_original - center)
```

Scaling is uniform and no rotation is applied. The exported normalized STEP has a longest axis of `0.0018 m`.

## 2D Output

```text
sheet:       A4 landscape
projection:  third angle
views:       front, top, right
display:     Hidden Lines Visible
tangent:     hidden
```

DXF, PDF, and SVG files are written under `output_root/techdraw/`.

## 3D Output

The renderer reads `output_root/normalized_step/` without further
normalization. It creates centered `1400 x 1000` isometric perspective PNGs
under:

```text
output_root/render_3D/transparent_shaded_edges_perspective/
output_root/render_3D/hlg_perspective/
output_root/render_3D/hlg_translucent_faces_perspective/
```

3D rendering runs by default. Use `-Skip3D` to disable it.
