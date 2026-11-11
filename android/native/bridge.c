#include "bridge.h"
#include <pthread.h>

static JavaVM* vm;
static jobject service;
static jmethodID protect_method;
static pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* machine, void* reserved) {
    vm = machine;
    return JNI_VERSION_1_6;
}

JNIEXPORT void JNICALL Java_io_regionlauncher_android_NativeCore_attach(JNIEnv* env, jclass clazz, jobject vpn) {
    pthread_mutex_lock(&mutex);
    if (service) (*env)->DeleteGlobalRef(env, service);
    service = (*env)->NewGlobalRef(env, vpn);
    jclass type = (*env)->GetObjectClass(env, vpn);
    protect_method = (*env)->GetMethodID(env, type, "protect", "(I)Z");
    (*env)->DeleteLocalRef(env, type);
    pthread_mutex_unlock(&mutex);
}

int rl_protect(int fd) {
    if (!vm) return 0;
    JNIEnv* env = NULL;
    int attached = 0;
    if ((*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK) {
        if ((*vm)->AttachCurrentThreadAsDaemon(vm, &env, NULL) != JNI_OK) return 0;
        attached = 1;
    }
    pthread_mutex_lock(&mutex);
    jobject target = service ? (*env)->NewLocalRef(env, service) : NULL;
    jmethodID method = protect_method;
    pthread_mutex_unlock(&mutex);
    int ok = target && method && (*env)->CallBooleanMethod(env, target, method, fd);
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); ok = 0; }
    if (target) (*env)->DeleteLocalRef(env, target);
    if (attached) (*vm)->DetachCurrentThread(vm);
    return ok;
}
