#import <JavaScriptCore/JavaScriptCore.h>
#include <Python.h>

// A fresh JavaScript context has no browser, filesystem, network, or native app objects.
static PyObject *evaluate(PyObject *module, PyObject *script) {
    Py_ssize_t length;
    const char *utf8 = PyUnicode_AsUTF8AndSize(script, &length);
    if (!utf8) return NULL;
    @autoreleasepool {
        NSString *source = [[NSString alloc] initWithBytes:utf8 length:length encoding:NSUTF8StringEncoding];
        JSContext *context = [[JSContext alloc] init];
        [context evaluateScript:@"globalThis.console = { log(value) { globalThis.clipDockOutput = value; } };"];
        [context evaluateScript:source];
        JSValue *output = context[@"clipDockOutput"];
        if (context.exception || !output.isString) {
            PyErr_SetString(PyExc_ValueError, "The player script did not return a result");
            return NULL;
        }
        return PyUnicode_FromString(output.toString.UTF8String);
    }
}

PyMODINIT_FUNC PyInit__clipdock_js(void) {
    static PyMethodDef methods[] = {{"evaluate", evaluate, METH_O, NULL}, {NULL}};
    static struct PyModuleDef module = {PyModuleDef_HEAD_INIT, "_clipdock_js", NULL, -1, methods};
    return PyModule_Create(&module);
}
