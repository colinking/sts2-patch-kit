extends SceneTree

# Headless-ish PNG export of the ban-overlay preview, driven by MegaDot. MegaDot's --headless mode
# uses a dummy renderer that can't capture a viewport, so run this WITHOUT --headless (a small window
# flashes); GPU rendering then populates the SubViewport and the capture succeeds:
#
#   "/Applications/MegaDot.app/Contents/MacOS/Godot" --path <project> \
#       --script tools/render_ban_overlay.gd -- --out=/tmp/ban_overlays_megadot.png

var _scene: Node
var _frames := 0
var _out := "/tmp/ban_overlays_megadot.png"
var _scale := 1.0

func _initialize() -> void:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--out="):
			_out = a.substr(6)
		elif a.begins_with("--scale="):
			_scale = a.substr(8).to_float()
	_scene = load("res://tools/ban_overlay_preview.tscn").instantiate()
	get_root().add_child(_scene)
	# Set after it's in the tree so the rebuild renders in a live viewport.
	_scene.render_scale = _scale

func _process(_delta: float) -> bool:
	# Let the SubViewport render a few frames before grabbing its texture.
	_frames += 1
	if _frames >= 6:
		_scene.save_png_path = _out
		_scene._capture()
		quit()
		return true
	return false
