@tool
extends Control

# Standalone MegaDot preview of the banned-character overlays — the same grid the in-game
# --banoverlay-shot harness produces, but rendered by MegaDot so it can be viewed/tweaked without
# launching the game. Top row is the five characters with no overlay; each following row applies one
# overlay PNG from ColinsPatchKit/assets/, stretched over the portraits the way the live mark is.
#
# Portraits come from the docs/images/charselect/ cache (dumped once by the in-game harness via
# --banoverlay-portraits=<dir>); missing portraits render as a gray placeholder. Open this scene in
# the editor to view it live, toggle Rebuild after changing assets, or toggle Save Png to export.
# Headless export goes through tools/render_ban_overlay.gd.

const ASSETS_DIR := "res://ColinsPatchKit/assets"
const PORTRAIT_DIR := "res://docs/images/charselect"
const CHAR_ORDER := ["IRONCLAD", "SILENT", "REGENT", "NECROBINDER", "DEFECT"]

const PORTRAIT_H := 240.0
const GAP_X := 28.0
const ROW_GAP_Y := 56.0
const LABEL_COL_W := 320.0
const HEADER_H := 48.0
const MARGIN := 40.0

@export var rebuild: bool = false:
	set(value):
		rebuild = false
		_build()

# Multiplies every layout metric (and font size) so the export can be rendered at retina / full-screen
# resolution. Text and the overlay PNG stay crisp; the 132x195 portraits upscale (source-limited).
@export var render_scale: float = 1.0:
	set(value):
		render_scale = maxf(value, 0.01)
		_build()

@export var save_png_path: String = "/tmp/ban_overlays_megadot.png"

@export var save_png: bool = false:
	set(value):
		save_png = false
		_capture()

var _subviewport: SubViewport

func _ready() -> void:
	_build()

func _abs(res_path: String) -> String:
	return ProjectSettings.globalize_path(res_path)

func _load_image(abs_path: String) -> Image:
	var img := Image.new()
	if img.load(abs_path) == OK:
		return img
	return null

# Source overlay names under the assets dir; handles both ".png" (editor) and ".png.import" (pck).
func _list_overlays() -> Array:
	var seen := {}
	var d := DirAccess.open(ASSETS_DIR)
	if d != null:
		for f in d.get_files():
			var n := f
			if n.ends_with(".import"):
				n = n.substr(0, n.length() - 7)
			elif n.ends_with(".remap"):
				n = n.substr(0, n.length() - 6)
			if n.to_lower().ends_with(".png"):
				seen[n] = true
	var names := seen.keys()
	names.sort()
	return names

func _make_cell(img: Image, pos: Vector2, sz: Vector2, placeholder: Color) -> Control:
	if img == null:
		var cr := ColorRect.new()
		cr.color = placeholder
		cr.position = pos
		cr.size = sz
		return cr
	var r := TextureRect.new()
	# ExpandMode before texture so Size isn't clamped up to the texture's native size.
	r.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	r.stretch_mode = TextureRect.STRETCH_SCALE
	r.texture = ImageTexture.create_from_image(img)
	r.position = pos
	r.size = sz
	return r

func _add_label(text: String, pos: Vector2, sz: Vector2, h: int, v: int, fs: int) -> void:
	var l := Label.new()
	l.text = text
	l.position = pos
	l.size = sz
	l.horizontal_alignment = h
	l.vertical_alignment = v
	l.add_theme_font_size_override("font_size", fs)
	l.add_theme_color_override("font_color", Color.BLACK) # readable on the white backdrop
	_subviewport.add_child(l)

func _build() -> void:
	for c in get_children():
		c.queue_free()
	_subviewport = null

	var overlays := _list_overlays()
	var portraits := {}
	for id in CHAR_ORDER:
		portraits[id] = _load_image(_abs(PORTRAIT_DIR + "/" + id + ".png"))

	var aspect := 132.0 / 195.0
	for id in CHAR_ORDER:
		if portraits[id] != null:
			aspect = float(portraits[id].get_width()) / float(portraits[id].get_height())
			break

	# Scaled layout metrics — render_scale enlarges everything for a retina / full-screen export.
	var s := render_scale
	var margin := MARGIN * s
	var label_col := LABEL_COL_W * s
	var header_h := HEADER_H * s
	var gap_x := GAP_X * s
	var row_gap := ROW_GAP_Y * s
	var header_fs := int(round(26.0 * s))
	var row_fs := int(round(22.0 * s))
	var cell_h := PORTRAIT_H * s
	var cell_w := cell_h * aspect

	var rows := 1 + overlays.size()
	var grid_left := margin + label_col
	var grid_top := margin + header_h
	var width := grid_left + CHAR_ORDER.size() * cell_w + (CHAR_ORDER.size() - 1) * gap_x + margin
	var height := grid_top + rows * cell_h + (rows - 1) * row_gap + margin

	custom_minimum_size = Vector2(width, height)
	size = Vector2(width, height)

	_subviewport = SubViewport.new()
	_subviewport.size = Vector2i(int(ceil(width)), int(ceil(height)))
	_subviewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_subviewport.transparent_bg = false
	add_child(_subviewport)

	# White backdrop so any overlay bleeding past a portrait's edge is obvious.
	var bg := ColorRect.new()
	bg.color = Color.WHITE
	bg.size = Vector2(width, height)
	_subviewport.add_child(bg)

	for i in CHAR_ORDER.size():
		var hx := grid_left + i * (cell_w + gap_x)
		_add_label(CHAR_ORDER[i], Vector2(hx, margin), Vector2(cell_w, header_h),
			HORIZONTAL_ALIGNMENT_CENTER, VERTICAL_ALIGNMENT_BOTTOM, header_fs)

	var overlay_imgs := {}
	for n in overlays:
		overlay_imgs[n] = _load_image(_abs(ASSETS_DIR + "/" + n))

	for r in rows:
		var y := grid_top + r * (cell_h + row_gap)
		var row_label: String = "Normal" if r == 0 else overlays[r - 1]
		var overlay_img = null if r == 0 else overlay_imgs[overlays[r - 1]]
		_add_label(row_label, Vector2(margin, y), Vector2(label_col - 16.0 * s, cell_h),
			HORIZONTAL_ALIGNMENT_RIGHT, VERTICAL_ALIGNMENT_CENTER, row_fs)
		for i in CHAR_ORDER.size():
			var x := grid_left + i * (cell_w + gap_x)
			var pos := Vector2(x, y)
			var sz := Vector2(cell_w, cell_h)
			_subviewport.add_child(_make_cell(portraits[CHAR_ORDER[i]], pos, sz, Color(0.3, 0.3, 0.3)))
			if overlay_img != null:
				_subviewport.add_child(_make_cell(overlay_img, pos, sz, Color(0, 0, 0, 0)))

	# Display the rendered grid so the editor 2D view shows it.
	var disp := TextureRect.new()
	disp.texture = _subviewport.get_texture()
	disp.position = Vector2.ZERO
	disp.size = Vector2(width, height)
	add_child(disp)

func _capture() -> void:
	if _subviewport == null:
		_build()
	var img := _subviewport.get_texture().get_image()
	var e := img.save_png(save_png_path)
	print("ban_overlay_preview: saved ", save_png_path, " err=", e,
		" (", _subviewport.size.x, "x", _subviewport.size.y, ")")
