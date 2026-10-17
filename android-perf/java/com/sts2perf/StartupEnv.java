package com.sts2perf;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.system.Os;
import android.util.Log;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStreamReader;

// Sets environment variables for the .NET runtime before it starts.
//
// Godot starts Mono (the game's .NET runtime) with no options of its own, and
// Mono reads its garbage collector's settings (MONO_GC_PARAMS) and its log
// settings (MONO_LOG_*) from the environment once, as it starts. The perf
// add-on runs inside that runtime, too late to set them, so it writes them to
// <files dir>/sts2perf_env (GcTuning.cs) and this applies them on the next
// start. It's a content provider only because Android creates the app's
// providers when the process starts, before any activity, so before Godot.
//
// Only the names below are set, and only values made of the characters the
// GC's options use. Anything that goes wrong is logged and skipped: the app
// starts as it would without this. And in case the options themselves ever
// stopped the runtime from starting: sts2perf_env.pending is created before
// they're set and deleted by the add-on once it runs (GcTuning.Sync), so if
// it's still there, the last start never got that far, and this one starts
// without them.
public final class StartupEnv extends ContentProvider {
    private static final String TAG = "STS2Perf";
    private static final String[] NAMES = {"MONO_GC_PARAMS", "MONO_LOG_LEVEL", "MONO_LOG_MASK"};

    @Override
    public boolean onCreate() {
        try {
            File dir = getContext().getFilesDir();
            // Tells the add-on where to write the file, and that this ran.
            Os.setenv("STS2PERF_FILES_DIR", dir.getAbsolutePath(), true);
            File file = new File(dir, "sts2perf_env");
            if (!file.isFile() || file.length() > 4096)
                return true;
            File pending = new File(dir, "sts2perf_env.pending");
            if (pending.exists()) {
                pending.delete();
                Log.w(TAG, "startup env: the last start with it didn't reach the add-on; starting without it once");
                return true;
            }
            pending.createNewFile();
            try (BufferedReader in = new BufferedReader(new InputStreamReader(new FileInputStream(file), "UTF-8"))) {
                String line;
                while ((line = in.readLine()) != null) {
                    int eq = line.indexOf('=');
                    if (eq <= 0)
                        continue;
                    String name = line.substring(0, eq).trim();
                    String value = line.substring(eq + 1).trim();
                    if (!allowed(name) || !value.matches("[A-Za-z0-9=,.:_-]{1,200}")) {
                        Log.w(TAG, "startup env: skipped " + line);
                        continue;
                    }
                    Os.setenv(name, value, true);
                    Log.i(TAG, "startup env: " + name + "=" + value);
                }
            }
        } catch (Throwable t) {
            Log.w(TAG, "startup env: not applied", t);
        }
        return true;
    }

    private static boolean allowed(String name) {
        for (String n : NAMES)
            if (n.equals(name))
                return true;
        return false;
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        return null;
    }

    @Override
    public String getType(Uri uri) {
        return null;
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }
}
