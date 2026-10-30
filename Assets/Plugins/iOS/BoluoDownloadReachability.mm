#import <Foundation/Foundation.h>
#import <SystemConfiguration/SystemConfiguration.h>
#import <netinet/in.h>
#import <ifaddrs.h>
#import <net/if.h>
#include <string.h>

/// Wi‑Fi / 有线是否已挂上（可不一定是默认路由）。
/// 用于纠正「Wi‑Fi + 蜂窝同时开」时 Unity internetReachability 误报 Carrier 的情况。
extern "C" int BoluoDownloadReachability_HasWifiOrEthernet(void)
{
    // 1) 默认路由可达且非 WWAN → 视为 Wi‑Fi/有线。
    struct sockaddr_in zeroAddress;
    memset(&zeroAddress, 0, sizeof(zeroAddress));
    zeroAddress.sin_len = sizeof(zeroAddress);
    zeroAddress.sin_family = AF_INET;

    SCNetworkReachabilityRef ref = SCNetworkReachabilityCreateWithAddress(
        kCFAllocatorDefault, (const struct sockaddr *)&zeroAddress);
    if (ref != NULL)
    {
        SCNetworkReachabilityFlags flags = 0;
        Boolean ok = SCNetworkReachabilityGetFlags(ref, &flags);
        CFRelease(ref);
        if (ok)
        {
            BOOL reachable = (flags & kSCNetworkReachabilityFlagsReachable) != 0;
            BOOL wwan = (flags & kSCNetworkReachabilityFlagsIsWWAN) != 0;
            if (reachable && !wwan)
                return 1;
        }
    }

    // 2) 双通道 / Wi‑Fi Assist：默认路由可能是蜂窝，但 en* 仍在 → 仍按 Wi‑Fi 策略静默下。
    struct ifaddrs *interfaces = NULL;
    if (getifaddrs(&interfaces) != 0)
        return 0;

    int found = 0;
    for (struct ifaddrs *addr = interfaces; addr != NULL; addr = addr->ifa_next)
    {
        if (addr->ifa_addr == NULL || addr->ifa_name == NULL)
            continue;
        if ((addr->ifa_flags & (IFF_UP | IFF_RUNNING)) != (IFF_UP | IFF_RUNNING))
            continue;
        if ((addr->ifa_flags & IFF_LOOPBACK) != 0)
            continue;

        // en0 等：Wi‑Fi；有线常见为 en* / eth*
        const char *name = addr->ifa_name;
        BOOL wifiLike = strncmp(name, "en", 2) == 0 || strncmp(name, "eth", 3) == 0;
        if (!wifiLike)
            continue;

        sa_family_t family = addr->ifa_addr->sa_family;
        if (family == AF_INET || family == AF_INET6)
        {
            found = 1;
            break;
        }
    }

    freeifaddrs(interfaces);
    return found;
}
