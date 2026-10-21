#import <Foundation/Foundation.h>

#if defined(__cplusplus)
extern "C" {
#endif

// Host (BoluoWanosLifecycle.swift) dlsym stubs. Full reclaim lives in BoluoMusicPlayerIOS.mm;
// these keep Scene lifecycle calls from no-op logging when symbols are required.

void BoluoMusicPlayerIOS_NotifyDidEnterBackground(void) __attribute__((weak));
void BoluoMusicPlayerIOS_PrepareForUnityResume(void) __attribute__((weak));
void BoluoMusicPlayerIOS_RestoreAfterUnityResume(void) __attribute__((weak));

void BoluoWanosLifecycle_NotifyDidEnterBackground(void)
{
    if (BoluoMusicPlayerIOS_NotifyDidEnterBackground)
        BoluoMusicPlayerIOS_NotifyDidEnterBackground();
    else
        NSLog(@"[BoluoWanosLifecycle] NotifyDidEnterBackground (stub)");
}

void BoluoWanosLifecycle_PrepareForUnityResume(void)
{
    if (BoluoMusicPlayerIOS_PrepareForUnityResume)
        BoluoMusicPlayerIOS_PrepareForUnityResume();
    else
        NSLog(@"[BoluoWanosLifecycle] PrepareForUnityResume (stub)");
}

void BoluoWanosLifecycle_RestoreAfterUnityResume(void)
{
    if (BoluoMusicPlayerIOS_RestoreAfterUnityResume)
        BoluoMusicPlayerIOS_RestoreAfterUnityResume();
    else
        NSLog(@"[BoluoWanosLifecycle] RestoreAfterUnityResume (stub)");
}

void BoluoWanosLifecycle_SetBackgroundPlayDisabled(int disabled)
{
    NSLog(@"[BoluoWanosLifecycle] SetBackgroundPlayDisabled=%d", disabled);
}

#if defined(__cplusplus)
}
#endif
