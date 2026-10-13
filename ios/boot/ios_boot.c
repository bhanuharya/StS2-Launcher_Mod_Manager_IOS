// ios_boot: a small GDExtension that runs at the CORE initialization level,
// before Godot loads themes, the boot splash, C# scripts, autoloads, or the
// main scene. It mounts the game's SlayTheSpire2.pck from the app's Documents
// folder (user://, reachable from the Files app) on top of the bootstrap pack
// that ships inside the app.
//
// The bootstrap pack is the main pack and holds the game's project settings
// plus our iOS overrides (.gdextension configs, extension list), so the game
// pack is mounted with replace_files = false and cannot shadow them.
//
// When the pck is missing or has the wrong size (a different game version
// than the one this build was compiled from), it swaps the main scene for a
// status screen that tells the player what to do instead of crashing.
//
// Must be the first entry in res://.godot/extension_list.cfg. It also exports
// load_all_fmod_plugins, which the iOS FMOD GDExtension resolves through
// flat lookup when it loads (it is linked with -undefined dynamic_lookup).

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#include "gdextension_interface.h"

#ifndef IOS_BOOT_PCK_NAME
#define IOS_BOOT_PCK_NAME "SlayTheSpire2.pck"
#endif

// Size in bytes of the pck this build was compiled against; 0 skips the check.
#ifndef IOS_BOOT_EXPECTED_PCK_SIZE
#define IOS_BOOT_EXPECTED_PCK_SIZE 0
#endif

#ifndef IOS_BOOT_GAME_VERSION
#define IOS_BOOT_GAME_VERSION "unknown"
#endif

#define STATUS_SCENE "res://addons/ios_boot/status.tscn"
#define EXPORT __attribute__((visibility("default")))

// Method hashes from Godot 4.5.1's extension_api.json.
#define HASH_LOAD_RESOURCE_PACK 708980503
#define HASH_SET_SETTING 402577236
#define HASH_GET_USER_DATA_DIR 201670096

// Opaque storage for Godot builtins (sizes from extension_api.json, 64-bit).
typedef struct {
	_Alignas(8) uint8_t data[8];
} GdString;
typedef struct {
	_Alignas(8) uint8_t data[8];
} GdStringName;
typedef struct {
	_Alignas(8) uint8_t data[24];
} GdVariant;

static GDExtensionInterfaceClassdbGetMethodBind classdb_get_method_bind;
static GDExtensionInterfaceGlobalGetSingleton global_get_singleton;
static GDExtensionInterfaceObjectMethodBindPtrcall ptrcall;
static GDExtensionInterfaceStringNameNewWithLatin1Chars string_name_new;
static GDExtensionInterfaceStringNewWithUtf8Chars string_new;
static GDExtensionInterfaceStringToUtf8Chars string_to_utf8;
static GDExtensionInterfaceGetVariantFromTypeConstructor variant_from_type;
static GDExtensionInterfaceVariantGetPtrDestructor ptr_destructor;
static GDExtensionInterfaceVariantDestroy variant_destroy;
static GDExtensionInterfacePrintWarning print_warning;
static GDExtensionInterfacePrintError print_error;

static void log_line(int is_error, const char *msg) {
	if (is_error) {
		print_error(msg, "ios_boot", __FILE__, __LINE__, 0);
	} else {
		// Plain prints aren't in the GDExtension interface; warnings reach the
		// log file and the device console, which is what we need.
		print_warning(msg, "ios_boot", __FILE__, __LINE__, 0);
	}
}

static void destroy_string(GdString *s) {
	ptr_destructor(GDEXTENSION_VARIANT_TYPE_STRING)(s);
}

static void destroy_string_name(GdStringName *s) {
	ptr_destructor(GDEXTENSION_VARIANT_TYPE_STRING_NAME)(s);
}

static GDExtensionMethodBindPtr method_bind(const char *cls, const char *method, GDExtensionInt hash) {
	GdStringName cls_name, method_name;
	string_name_new(&cls_name, cls, 0);
	string_name_new(&method_name, method, 0);
	GDExtensionMethodBindPtr mb = classdb_get_method_bind(&cls_name, &method_name, hash);
	destroy_string_name(&cls_name);
	destroy_string_name(&method_name);
	return mb;
}

static GDExtensionObjectPtr singleton(const char *name) {
	GdStringName sn;
	string_name_new(&sn, name, 0);
	GDExtensionObjectPtr obj = global_get_singleton(&sn);
	destroy_string_name(&sn);
	return obj;
}

static char *string_to_c(const GdString *s) {
	GDExtensionInt len = string_to_utf8(s, NULL, 0);
	char *buf = malloc((size_t)len + 1);
	string_to_utf8(s, buf, len);
	buf[len] = '\0';
	return buf;
}

static void set_string_setting(GDExtensionObjectPtr project_settings, const char *name, const char *value) {
	GDExtensionMethodBindPtr mb = method_bind("ProjectSettings", "set_setting", HASH_SET_SETTING);
	if (!mb) {
		log_line(1, "ProjectSettings.set_setting not found");
		return;
	}
	GdString key, str;
	GdVariant var;
	string_new(&key, name);
	string_new(&str, value);
	variant_from_type(GDEXTENSION_VARIANT_TYPE_STRING)(&var, &str);
	GDExtensionConstTypePtr args[2] = { &key, &var };
	ptrcall(mb, project_settings, args, NULL);
	variant_destroy(&var);
	destroy_string(&str);
	destroy_string(&key);
}

static void show_status_screen(GDExtensionObjectPtr project_settings, const char *status) {
	set_string_setting(project_settings, "ios_boot/status", status);
	set_string_setting(project_settings, "application/run/main_scene", STATUS_SCENE);
}

static void mount_game_pack(void) {
	GDExtensionObjectPtr os = singleton("OS");
	GDExtensionObjectPtr project_settings = singleton("ProjectSettings");
	GDExtensionMethodBindPtr get_dir = method_bind("OS", "get_user_data_dir", HASH_GET_USER_DATA_DIR);
	GDExtensionMethodBindPtr load_pack = method_bind("ProjectSettings", "load_resource_pack", HASH_LOAD_RESOURCE_PACK);
	if (!os || !project_settings || !get_dir || !load_pack) {
		log_line(1, "engine API lookup failed; is this Godot 4.5.x?");
		return;
	}

	GdString dir_str;
	string_new(&dir_str, "");
	ptrcall(get_dir, os, NULL, &dir_str);
	char *dir = string_to_c(&dir_str);
	destroy_string(&dir_str);

	size_t path_len = strlen(dir) + 1 + strlen(IOS_BOOT_PCK_NAME) + 1;
	char *path = malloc(path_len);
	snprintf(path, path_len, "%s/%s", dir, IOS_BOOT_PCK_NAME);
	free(dir);

	char msg[1024];
	struct stat st;
	long long expected = (long long)IOS_BOOT_EXPECTED_PCK_SIZE;
	if (stat(path, &st) != 0) {
		snprintf(msg, sizeof msg,
				"missing|%s|Copy %s (game version %s) into this app's folder in the Files app, then reopen the game.",
				path, IOS_BOOT_PCK_NAME, IOS_BOOT_GAME_VERSION);
		log_line(1, msg);
		show_status_screen(project_settings, msg);
	} else if (expected > 0 && (long long)st.st_size != expected) {
		snprintf(msg, sizeof msg,
				"mismatch|%s|%s is %lld bytes but this build expects %lld bytes (game version %s). Copy the matching file, or rebuild the app for your game version.",
				path, IOS_BOOT_PCK_NAME, (long long)st.st_size, expected, IOS_BOOT_GAME_VERSION);
		log_line(1, msg);
		show_status_screen(project_settings, msg);
	} else {
		GdString pack;
		string_new(&pack, path);
		uint8_t replace_files = 0;
		int64_t offset = 0;
		uint8_t ok = 0;
		GDExtensionConstTypePtr args[3] = { &pack, &replace_files, &offset };
		ptrcall(load_pack, project_settings, args, &ok);
		destroy_string(&pack);
		if (ok) {
			snprintf(msg, sizeof msg, "mounted|%s|%lld bytes", path, (long long)st.st_size);
			log_line(0, msg);
			set_string_setting(project_settings, "ios_boot/status", msg);
		} else {
			snprintf(msg, sizeof msg,
					"unreadable|%s|Godot could not open %s. The file may be damaged; copy it again.",
					path, IOS_BOOT_PCK_NAME);
			log_line(1, msg);
			show_status_screen(project_settings, msg);
		}
	}
	free(path);
}

static void on_initialize(void *userdata, GDExtensionInitializationLevel level) {
	(void)userdata;
	if (level == GDEXTENSION_INITIALIZATION_CORE) {
		mount_game_pack();
	}
}

static void on_deinitialize(void *userdata, GDExtensionInitializationLevel level) {
	(void)userdata;
	(void)level;
}

EXPORT GDExtensionBool ios_boot_init(GDExtensionInterfaceGetProcAddress get_proc,
		GDExtensionClassLibraryPtr library, GDExtensionInitialization *r_init) {
	(void)library;
	classdb_get_method_bind = (GDExtensionInterfaceClassdbGetMethodBind)get_proc("classdb_get_method_bind");
	global_get_singleton = (GDExtensionInterfaceGlobalGetSingleton)get_proc("global_get_singleton");
	ptrcall = (GDExtensionInterfaceObjectMethodBindPtrcall)get_proc("object_method_bind_ptrcall");
	string_name_new = (GDExtensionInterfaceStringNameNewWithLatin1Chars)get_proc("string_name_new_with_latin1_chars");
	string_new = (GDExtensionInterfaceStringNewWithUtf8Chars)get_proc("string_new_with_utf8_chars");
	string_to_utf8 = (GDExtensionInterfaceStringToUtf8Chars)get_proc("string_to_utf8_chars");
	variant_from_type = (GDExtensionInterfaceGetVariantFromTypeConstructor)get_proc("get_variant_from_type_constructor");
	ptr_destructor = (GDExtensionInterfaceVariantGetPtrDestructor)get_proc("variant_get_ptr_destructor");
	variant_destroy = (GDExtensionInterfaceVariantDestroy)get_proc("variant_destroy");
	print_warning = (GDExtensionInterfacePrintWarning)get_proc("print_warning");
	print_error = (GDExtensionInterfacePrintError)get_proc("print_error");

	if (!classdb_get_method_bind || !global_get_singleton || !ptrcall || !string_name_new || !string_new ||
			!string_to_utf8 || !variant_from_type || !ptr_destructor || !variant_destroy || !print_warning ||
			!print_error) {
		return 0;
	}

	r_init->minimum_initialization_level = GDEXTENSION_INITIALIZATION_CORE;
	r_init->userdata = NULL;
	r_init->initialize = on_initialize;
	r_init->deinitialize = on_deinitialize;
	return 1;
}

// FMOD's iOS GDExtension expects the app to provide this (it normally comes
// from the Godot editor's FMOD export plugin). The game ships no static FMOD
// plugins, so report zero.
typedef struct FmodIosInterface FmodIosInterface;

EXPORT __attribute__((used)) uint32_t *load_all_fmod_plugins(FmodIosInterface *p_interface, uint32_t *r_count) {
	(void)p_interface;
	if (r_count) {
		*r_count = 0;
	}
	return NULL;
}
