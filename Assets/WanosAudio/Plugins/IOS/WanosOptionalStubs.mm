// Optional WIAEngine exports that some libWIAEngine.a builds omit.
// C# calls these via TryCallOptional; iOS IL2CPP still needs link-time symbols.
#import <Foundation/Foundation.h>
#include <stdint.h>

extern "C" {

// Present in libWIAEngine — used by _impl wrappers for older GameAssembly exports.
extern void SetLogEnabled(bool enabled);
extern void SetLogLevel(int level);

void EngineStart(void) {}
void EngineResume(void) {}
void EngineStop(void) {}

// Legacy IL2CPP names (before EntryPoint was fixed to SetLogEnabled / SetLogLevel).
void SetLogEnabled_impl(bool enabled) { SetLogEnabled(enabled); }
void SetLogLevel_impl(int level) { SetLogLevel(level); }

// Matches WanosResult (Success = 0).
int RegisterEventStateCallback(void *callback)
{
    (void)callback;
    return 0;
}

typedef struct {
    float px, py, pz;
    float fx, fy, fz;
    float ux, uy, uz;
} WanosPose6DofStub;

void SetEmitterPositionAndRadius(uint64_t objId, WanosPose6DofStub posInfo, float radius)
{
    (void)objId;
    (void)posInfo;
    (void)radius;
}

int StopAllVoicesNative(int fadeMs)
{
    (void)fadeMs;
    return 0;
}

int StopVoiceWithFadeNative(uint64_t voiceId, int fadeMs)
{
    (void)voiceId;
    (void)fadeMs;
    return 0;
}

} // extern "C"
