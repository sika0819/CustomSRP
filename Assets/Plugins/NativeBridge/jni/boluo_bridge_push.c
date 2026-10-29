/*
 * IMP push + iOS host Notify/Query/Sign forwarder.
 * Android: libboluoimp.so
 * iOS: compiled into UnityFramework (__Internal)
 */
#include <stdint.h>
#include <stdatomic.h>
#include <string.h>

typedef void (*BoluoPushFn)(int32_t type, int32_t a, int32_t b, int32_t c);
typedef void (*BoluoImpNotifyFn)(int32_t type, int32_t a, int32_t b, int32_t c);
typedef const char *(*BoluoImpQueryFn)(
    int32_t type, int32_t a, int32_t b, int32_t c, const char *inBlob);
typedef const char *(*BoluoImpSignFn)(const char *jsonBody);

static _Atomic(BoluoPushFn) g_push;
static _Atomic(BoluoImpNotifyFn) g_notify;
static _Atomic(BoluoImpQueryFn) g_query;
static _Atomic(BoluoImpSignFn) g_sign;

#if defined(__ANDROID__)
#include <jni.h>
#endif

#ifdef __cplusplus
extern "C" {
#endif

void BoluoImp_RegisterPush(BoluoPushFn fn)
{
    atomic_store(&g_push, fn);
}

void BoluoImp_UnregisterPush(void)
{
    atomic_store(&g_push, NULL);
}

void BoluoImp_Push(int32_t type, int32_t a, int32_t b, int32_t c)
{
    BoluoPushFn fn = atomic_load(&g_push);
    if (fn != NULL)
        fn(type, a, b, c);
}

void BoluoImp_RegisterHost(BoluoImpNotifyFn notifyFn, BoluoImpQueryFn queryFn)
{
    atomic_store(&g_notify, notifyFn);
    atomic_store(&g_query, queryFn);
}

void BoluoImp_RegisterSign(BoluoImpSignFn signFn)
{
    atomic_store(&g_sign, signFn);
}

void BoluoImp_Notify(int32_t type, int32_t a, int32_t b, int32_t c)
{
    BoluoImpNotifyFn fn = atomic_load(&g_notify);
    if (fn != NULL)
        fn(type, a, b, c);
}

const char *BoluoImp_Query(
    int32_t type, int32_t a, int32_t b, int32_t c, const char *inBlob)
{
    BoluoImpQueryFn fn = atomic_load(&g_query);
    if (fn == NULL)
        return "-2|";
    return fn(type, a, b, c, inBlob);
}

const char *BoluoImp_GenerateSign(const char *jsonBody)
{
    BoluoImpSignFn fn = atomic_load(&g_sign);
    if (fn == NULL)
        return NULL;
    return fn(jsonBody != NULL ? jsonBody : "{}");
}

#if defined(__ANDROID__)
#include <dlfcn.h>
#include <android/log.h>

static void BoluoPumpUnityForMedia(void)
{
    static void (*pauseFn)(int) = NULL;
    static void (*batchFn)(void) = NULL;
    static int (*isPausedFn)(void) = NULL;
    static int resolved = 0;
    if (!resolved)
    {
        resolved = 1;
        pauseFn = (void (*)(int))dlsym(RTLD_DEFAULT, "UnityPause");
        batchFn = (void (*)(void))dlsym(RTLD_DEFAULT, "UnityBatchPlayerLoop");
        isPausedFn = (int (*)(void))dlsym(RTLD_DEFAULT, "UnityIsPaused");
        if (batchFn == NULL)
            __android_log_print(ANDROID_LOG_WARN, "BoluoImp",
                "UnityBatchPlayerLoop missing — host must keep GameActivity loop");
    }
    if (isPausedFn && isPausedFn() != 0 && pauseFn)
        pauseFn(0);
    if (batchFn)
    {
        int i;
        for (i = 0; i < 4; i++)
            batchFn();
    }
}

JNIEXPORT jboolean JNICALL
Java_com_wanos_boluo_unity_BoluoUnityPlugin_nativePush(
    JNIEnv *env, jobject thiz,
    jint type, jint a, jint b, jint c)
{
    (void)env;
    (void)thiz;
    /* 接收端还没 RegisterPush 时 Push 会被丢掉，调用方必须知道。 */
    if (atomic_load(&g_push) == NULL)
        return JNI_FALSE;
    int pump = type == 104 || type == 103;
    if (pump)
        BoluoPumpUnityForMedia();
    BoluoImp_Push((int32_t)type, (int32_t)a, (int32_t)b, (int32_t)c);
    if (pump)
        BoluoPumpUnityForMedia();
    return JNI_TRUE;
}
#endif

#ifdef __cplusplus
}
#endif
