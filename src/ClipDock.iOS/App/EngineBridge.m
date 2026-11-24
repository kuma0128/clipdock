#import "EngineBridge.h"
#include <Python.h>

static BOOL startPython(void) {
    NSString *bundle = NSBundle.mainBundle.resourcePath;
    PyPreConfig preconfig;
    PyPreConfig_InitIsolatedConfig(&preconfig);
    preconfig.utf8_mode = 1;
    if (PyStatus_Exception(Py_PreInitialize(&preconfig))) return NO;

    PyConfig config;
    PyConfig_InitIsolatedConfig(&config);
    config.write_bytecode = 0;
    config.install_signal_handlers = 0;
    config.buffered_stdio = 0;
    NSString *home = [bundle stringByAppendingPathComponent:@"python"];
    PyStatus status = PyConfig_SetBytesString(&config, &config.home, home.fileSystemRepresentation);
    config.module_search_paths_set = 1;
    for (NSString *relative in @[@"python/lib/python3.14", @"python/lib/python3.14/lib-dynload", @"packages"]) {
        if (PyStatus_Exception(status)) break;
        NSString *path = [bundle stringByAppendingPathComponent:relative];
        wchar_t *wide = Py_DecodeLocale(path.fileSystemRepresentation, NULL);
        if (!wide) { PyConfig_Clear(&config); return NO; }
        status = PyWideStringList_Append(&config.module_search_paths, wide);
        PyMem_RawFree(wide);
    }
    if (!PyStatus_Exception(status)) status = Py_InitializeFromConfig(&config);
    PyConfig_Clear(&config);
    if (PyStatus_Exception(status)) return NO;
    PyEval_SaveThread();
    return YES;
}

static PyObject *report(PyObject *context, PyObject *value) {
    BOOL (^progress)(NSInteger) = (__bridge id)PyCapsule_GetPointer(context, "ClipDock.progress");
    long percent = PyLong_AsLong(value);
    if (PyErr_Occurred()) return NULL;
    return PyBool_FromLong(progress(percent));
}

NSString *CDDownload(NSString *link, NSString *directory, BOOL (^progress)(NSInteger)) {
    static dispatch_once_t once;
    static BOOL ready;
    dispatch_once(&once, ^{ ready = startPython(); });
    if (!ready) return nil;

    PyGILState_STATE gil = PyGILState_Ensure();
    static PyMethodDef method = {"progress", report, METH_O, NULL};
    PyObject *module = PyImport_ImportModule("media");
    PyObject *context = PyCapsule_New((__bridge void *)progress, "ClipDock.progress", NULL);
    PyObject *callback = context ? PyCFunction_New(&method, context) : NULL;
    PyObject *result = module && callback
        ? PyObject_CallMethod(module, "run", "ssO", link.UTF8String, directory.fileSystemRepresentation, callback)
        : NULL;
    const char *json = result ? PyUnicode_AsUTF8(result) : NULL;
    NSString *response = json ? [NSString stringWithUTF8String:json] : nil;
    if (PyErr_Occurred()) {
#if DEBUG
        PyErr_Print();
#else
        PyErr_Clear();
#endif
    }
    Py_XDECREF(result);
    Py_XDECREF(callback);
    Py_XDECREF(context);
    Py_XDECREF(module);
    PyGILState_Release(gil);
    return response;
}
