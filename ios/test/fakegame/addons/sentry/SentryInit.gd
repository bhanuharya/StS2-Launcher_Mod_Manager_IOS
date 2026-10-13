extends Node

# The real game's Sentry autoload; the iOS build must drop it.
func _ready() -> void:
	print("FAKEGAME sentryAutoload: FAIL (should have been removed)")
