extends Control

# Shown instead of the game when ios_boot could not mount SlayTheSpire2.pck.
# ios_boot leaves "kind|path|message" in the ios_boot/status setting.

func _ready() -> void:
	var raw: String = ProjectSettings.get_setting("ios_boot/status", "unknown||No status from ios_boot.")
	var parts := raw.split("|", true, 2)
	var kind := parts[0] if parts.size() > 0 else "unknown"
	var path := parts[1] if parts.size() > 1 else ""
	var message := parts[2] if parts.size() > 2 else raw
	print("[ios_boot] status screen: ", raw)

	var titles := {
		"missing": "Game data not found",
		"mismatch": "Game data is a different version",
		"unreadable": "Game data could not be opened",
	}

	var bg := ColorRect.new()
	bg.color = Color(0.08, 0.09, 0.12)
	bg.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(bg)

	var box := VBoxContainer.new()
	box.set_anchors_preset(Control.PRESET_FULL_RECT)
	box.offset_left = 64
	box.offset_right = -64
	box.offset_top = 48
	box.offset_bottom = -48
	box.add_theme_constant_override("separation", 24)
	add_child(box)

	var title := Label.new()
	title.text = titles.get(kind, "Startup problem")
	title.add_theme_font_size_override("font_size", 44)
	title.add_theme_color_override("font_color", Color(0.95, 0.77, 0.06))
	box.add_child(title)

	var body := Label.new()
	body.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	body.add_theme_font_size_override("font_size", 28)
	body.text = message + "\n\nIn the Files app: On My iPhone > " \
		+ str(ProjectSettings.get_setting("ios_boot/app_name", "this app")) \
		+ " > " + path.get_file()
	box.add_child(body)

	var detail := Label.new()
	detail.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	detail.add_theme_font_size_override("font_size", 18)
	detail.add_theme_color_override("font_color", Color(0.6, 0.6, 0.65))
	detail.text = "Expected at: " + path
	box.add_child(detail)
