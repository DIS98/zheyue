// JNI 胶水层：把 Java 的 jstring 转成 C 字符串，再调用 Go 导出的纯 C 函数。
// 这样 Go 侧完全不用碰 JNIEnv，避免 cgo 里操作 JNI 函数表的坑。
//
// 符号名对应 Kotlin 侧 com.clashdeck.android.core.Native 的 external 方法。

#include <jni.h>
#include <stdlib.h>
#include <string.h>

// Go(c-shared) 导出的纯 C 函数
extern int cd_start_core(char* home, char* cfg, int fd, char* stack, char* gateway, char* dns);
extern int cd_stop_core(void);
extern int cd_set_mode(char* mode);
extern char* cd_last_error(void);
extern char* cd_version(void);
extern int cd_api_port(void);

static char* toCString(JNIEnv* env, jstring s) {
    if (s == NULL) return NULL;
    const char* utf = (*env)->GetStringUTFChars(env, s, NULL);
    if (utf == NULL) return NULL;
    char* out = strdup(utf);
    (*env)->ReleaseStringUTFChars(env, s, utf);
    return out;
}

static jstring toJString(JNIEnv* env, char* s) {
    if (s == NULL) return NULL;
    return (*env)->NewStringUTF(env, s);
}

JNIEXPORT jint JNICALL
Java_com_clashdeck_android_core_Native_startCore(JNIEnv* env, jclass clz,
                                                 jstring home, jstring cfg, jint fd,
                                                 jstring stack, jstring gateway, jstring dns) {
    char* cHome = toCString(env, home);
    char* cCfg = toCString(env, cfg);
    char* cStack = toCString(env, stack);
    char* cGateway = toCString(env, gateway);
    char* cDns = toCString(env, dns);

    int rc = -1;
    if (cHome != NULL && cCfg != NULL) {
        rc = cd_start_core(cHome, cCfg, (int) fd, cStack, cGateway, cDns);
    }

    free(cHome);
    free(cCfg);
    free(cStack);
    free(cGateway);
    free(cDns);
    return (jint) rc;
}

JNIEXPORT jint JNICALL
Java_com_clashdeck_android_core_Native_stopCore(JNIEnv* env, jclass clz) {
    return (jint) cd_stop_core();
}

JNIEXPORT jint JNICALL
Java_com_clashdeck_android_core_Native_setMode(JNIEnv* env, jclass clz, jstring mode) {
    char* cMode = toCString(env, mode);
    int rc = -1;
    if (cMode != NULL) {
        rc = cd_set_mode(cMode);
        free(cMode);
    }
    return (jint) rc;
}

// 控制端实际端口：配置端口被占用时内核会换一个，界面必须问这里。
JNIEXPORT jint JNICALL
Java_com_clashdeck_android_core_Native_apiPort(JNIEnv* env, jclass clz) {
    return cd_api_port();
}

JNIEXPORT jstring JNICALL
Java_com_clashdeck_android_core_Native_lastError(JNIEnv* env, jclass clz) {
    return toJString(env, cd_last_error());
}

JNIEXPORT jstring JNICALL
Java_com_clashdeck_android_core_Native_coreVersion(JNIEnv* env, jclass clz) {
    return toJString(env, cd_version());
}
