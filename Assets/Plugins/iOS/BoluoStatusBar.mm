#import <UIKit/UIKit.h>
#import <objc/runtime.h>

extern UIViewController* UnityGetGLViewController(void);

static BOOL sBoluoHideStatusBar = NO;
static BOOL sBoluoHomeIndicatorAutoHidden = NO;

static BOOL BoluoPrefersStatusBarHidden(id self, SEL _cmd)
{
#pragma unused(self, _cmd)
    return sBoluoHideStatusBar;
}

static BOOL BoluoPrefersHomeIndicatorAutoHidden(id self, SEL _cmd)
{
#pragma unused(self, _cmd)
    return sBoluoHomeIndicatorAutoHidden;
}

static void BoluoInstallStatusBarOnClass(Class cls)
{
    if (cls == Nil)
        return;
    SEL sel = @selector(prefersStatusBarHidden);
    Method m = class_getInstanceMethod(cls, sel);
    if (m != NULL)
        method_setImplementation(m, (IMP)BoluoPrefersStatusBarHidden);
    else
        class_addMethod(cls, sel, (IMP)BoluoPrefersStatusBarHidden, "B@:");
}

static void BoluoInstallHomeIndicatorOnClass(Class cls)
{
    if (cls == Nil)
        return;
    SEL sel = @selector(prefersHomeIndicatorAutoHidden);
    Method m = class_getInstanceMethod(cls, sel);
    if (m != NULL)
        method_setImplementation(m, (IMP)BoluoPrefersHomeIndicatorAutoHidden);
    else
        class_addMethod(cls, sel, (IMP)BoluoPrefersHomeIndicatorAutoHidden, "B@:");
}

static void BoluoInstallImmersiveHooksOnce(void)
{
    static const char *names[] = {
        "UnityViewController",
        "UnityPortraitOnlyViewController",
        "UnityLandscapeLeftOnlyViewController",
        "UnityLandscapeRightOnlyViewController",
    };
    for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); ++i)
    {
        Class c = NSClassFromString([NSString stringWithUTF8String:names[i]]);
        BoluoInstallStatusBarOnClass(c);
        BoluoInstallHomeIndicatorOnClass(c);
    }
}

static void BoluoRequestChromeUpdate(UIViewController *unityVc)
{
    if (unityVc != nil)
    {
        [unityVc setNeedsStatusBarAppearanceUpdate];
        if (@available(iOS 11.0, *))
            [unityVc setNeedsUpdateOfHomeIndicatorAutoHidden];
    }

    for (UIScene *scene in UIApplication.sharedApplication.connectedScenes)
    {
        if (![scene isKindOfClass:[UIWindowScene class]])
            continue;
        for (UIWindow *window in ((UIWindowScene *)scene).windows)
        {
            UIViewController *root = window.rootViewController;
            if (root == nil)
                continue;
            [root setNeedsStatusBarAppearanceUpdate];
            if (@available(iOS 11.0, *))
                [root setNeedsUpdateOfHomeIndicatorAutoHidden];
        }
    }
}

static NSString * const kBoluoChromeImmersiveNotif = @"BoluoChromeImmersiveDidChange";

extern "C" void BoluoStatusBar_SetHidden(int hidden)
{
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        BoluoInstallImmersiveHooksOnce();
    });

    BOOL on = hidden != 0;
    sBoluoHideStatusBar = on;
    sBoluoHomeIndicatorAutoHidden = on;

    dispatch_async(dispatch_get_main_queue(), ^{
        [[NSNotificationCenter defaultCenter] postNotificationName:kBoluoChromeImmersiveNotif
                                                            object:nil
                                                          userInfo:@{ @"hidden": @(on) }];
        BoluoRequestChromeUpdate(UnityGetGLViewController());
    });
}
