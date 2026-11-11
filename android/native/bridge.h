#include <jni.h>
#include <stdlib.h>
static const char* rl_chars(JNIEnv* e, jstring s) { return (*e)->GetStringUTFChars(e, s, NULL); }
static void rl_release(JNIEnv* e, jstring s, const char* p) { (*e)->ReleaseStringUTFChars(e, s, p); }
static jstring rl_string(JNIEnv* e, const char* p) { return (*e)->NewStringUTF(e, p); }
int rl_protect(int fd);
