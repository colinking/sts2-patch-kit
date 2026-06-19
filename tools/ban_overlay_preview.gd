@tool
extends Control

# Standalone MegaDot preview of the banned-character overlays, rendered without launching the game.
# Base rows are "Normal" (no overlay) plus one per overlay PNG in ColinsPatchKit/assets/. Each base
# row is rendered in BOTH in-game character-select states so the overlay can be judged against the
# real look: "· idle" (unselected — the game's hsv shader desaturates/dims the portrait) and
# "· hover" (full saturation, slight brighten, and the 1.1x hover zoom). The red X carries no hsv,
# matching the game (the mark is unaffected by the icon shader), so it stays constant across states.
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

# The game's own hsv shader (dumped from res://shaders/hsv.gdshader), applied to portraits so the
# preview matches the in-game look: unselected characters are desaturated/dimmed (s<1, v<1), the
# hovered one is full/brightened (s=1, v>1). Only h/s/v differ between states.
const PORTRAIT_HSV_SHADER := "
shader_type canvas_item;
uniform float h: hint_range(0,1) = 1;
uniform float s: hint_range(0,5) = 1;
uniform float v = 1;
varying vec4 modulate_color;
void vertex() { modulate_color = COLOR; }
void fragment() {
	mat3 RGB_to_YIQ = mat3(
		vec3(0.2989,  0.5959,  0.2115),
		vec3(0.5870, -0.2774, -0.5229),
		vec3(0.1140, -0.3216,  0.3114));
	vec4 col = texture(TEXTURE, UV);
	col.rgb = RGB_to_YIQ * col.rgb;
	float hue = 1.0 - h;
	hue = mix(0, 6.283185, hue);
	float sin_hue = sin(hue);
	float cos_hue = cos(hue);
	mat3 hue_shift = mat3(vec3(1.0, 0, 0), vec3(0, cos_hue, -sin_hue), vec3(0, sin_hue, cos_hue));
	col.rgb *= hue_shift;
	mat3 sat_shift = mat3(vec3(1.0, 0, 0), vec3(0, s, 0), vec3(0, 0, s));
	col.rgb = sat_shift * col.rgb;
	col.rgb = mix(vec3(0,0,0), col.rgb, v);
	col.rgb = inverse(RGB_to_YIQ) * col.rgb;
	COLOR = col;
	COLOR *= modulate_color;
}"

# Per-state hsv params the game uses (NotSelected vs SelectedLocally / OnFocus), and the hover zoom.
# Each base row is rendered in both states. Note idle v is 0.8, not 0.4 — 0.4 is the *remote*-select
# brightness; the unselected resting state only desaturates (s=0.2) and dims slightly (v=0.8).
const STATE_S := {&"idle": 0.2, &"hover": 1.0}
const STATE_V := {&"idle": 0.8, &"hover": 1.1}
const HOVER_SCALE := 1.1

@export var rebuild: bool = false:
	set(value):
		rebuild = false
		_build()

# Vibrancy knob: extra saturation on top of each state's game value (effective s = state_s * (1+v)).
# 0 = exact in-game look. Editor slider; headless via --vibrancy=<n>.
@export_range(0.0, 1.0, 0.01) var vibrancy: float = 0.0:
	set(value):
		vibrancy = clampf(value, 0.0, 1.0)
		_build()

# Overlay (red X) hover treatment: on hover the X is run through hsv with this saturation and value,
# so it pops/glows; on idle it's left constant (vanilla). s=v=1.0 = no change, s=v=1.5 = the
# saturated-and-brightened-on-hover look. Editor sliders; headless via --overlay-hover-s/v=<n>.
@export_range(0.0, 3.0, 0.05) var overlay_hover_s: float = 1.0:
	set(value):
		overlay_hover_s = maxf(value, 0.0)
		_build()

@export_range(1.0, 2.0, 0.05) var overlay_hover_v: float = 1.2:
	set(value):
		overlay_hover_v = maxf(value, 0.0)
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

func _make_cell(img: Image, pos: Vector2, sz: Vector2, placeholder: Color, mat: Material = null) -> Control:
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
	r.material = mat
	return r

func _portrait_material(base_s: float, base_v: float) -> ShaderMaterial:
	# Game's hsv shader at this state, with the vibrancy knob boosting saturation on top.
	return _hsv_material(base_s * (1.0 + vibrancy), base_v)

func _hsv_material(s_val: float, v_val: float) -> ShaderMaterial:
	var m := ShaderMaterial.new()
	m.shader = Shader.new()
	m.shader.code = PORTRAIT_HSV_SHADER
	m.set_shader_parameter("h", 1.0)
	m.set_shader_parameter("s", s_val)
	m.set_shader_parameter("v", v_val)
	return m

func _make_mask(mask_img: Image, pos: Vector2, sz: Vector2) -> Control:
	# A TextureRect of the ragged mask with ClipChildren=Only, mirroring the game's Mask node so
	# every descendant (portrait + overlay) is clipped to the torn-edge shape.
	var m := TextureRect.new()
	m.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	m.stretch_mode = TextureRect.STRETCH_SCALE
	if mask_img != null:
		m.texture = ImageTexture.create_from_image(mask_img)
		m.clip_children = CanvasItem.CLIP_CHILDREN_ONLY
	m.position = pos
	m.size = sz
	return m

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
	# The game's ragged-edge button mask; we clip each cell to it so the overlay shows the same
	# torn edge it gets in-game (dumped alongside the portraits by --banoverlay-portraits).
	var mask_img := _load_image(_abs(PORTRAIT_DIR + "/_button_mask.png"))

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

	# Each base row (Normal + one per overlay) is rendered twice: idle (dimmed) and hover (bright+zoom).
	var base_rows := 1 + overlays.size()
	var total_rows := base_rows * 2
	var grid_left := margin + label_col
	var grid_top := margin + header_h
	var width := grid_left + CHAR_ORDER.size() * cell_w + (CHAR_ORDER.size() - 1) * gap_x + margin
	var height := grid_top + total_rows * cell_h + (total_rows - 1) * row_gap + margin

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

	var ridx := 0
	for br in base_rows:
		var base_label: String = "Normal" if br == 0 else overlays[br - 1]
		var overlay_img = null if br == 0 else overlay_imgs[overlays[br - 1]]
		for state in [&"idle", &"hover"]:
			var y := grid_top + ridx * (cell_h + row_gap)
			var hovered: bool = state == &"hover"
			# Portrait gets the game's hsv at this state's params; the overlay gets no shader (the
			# game's mark is unaffected by hsv, so the red X stays constant across states).
			var portrait_mat := _portrait_material(STATE_S[state], STATE_V[state])
			_add_label("%s · %s" % [base_label, state], Vector2(margin, y),
				Vector2(label_col - 16.0 * s, cell_h), HORIZONTAL_ALIGNMENT_RIGHT, VERTICAL_ALIGNMENT_CENTER, row_fs)
			for i in CHAR_ORDER.size():
				var x := grid_left + i * (cell_w + gap_x)
				var pos := Vector2(x, y)
				var sz := Vector2(cell_w, cell_h)
				# Nest portrait + overlay under the ragged mask so both share the same torn edge.
				var mc := _make_mask(mask_img, pos, sz)
				if hovered:
					mc.pivot_offset = sz * 0.5  # zoom around the cell centre, like the in-game hover
					mc.scale = Vector2(HOVER_SCALE, HOVER_SCALE)
				_subviewport.add_child(mc)
				mc.add_child(_make_cell(portraits[CHAR_ORDER[i]], Vector2.ZERO, sz, Color(0.3, 0.3, 0.3), portrait_mat))
				if overlay_img != null:
					# Shadow is baked into the overlay PNG now. Idle: constant. Hover: saturate + brighten.
					var overlay_mat: ShaderMaterial = _hsv_material(overlay_hover_s, overlay_hover_v) if hovered else null
					mc.add_child(_make_cell(overlay_img, Vector2.ZERO, sz, Color(0, 0, 0, 0), overlay_mat))
			ridx += 1

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
