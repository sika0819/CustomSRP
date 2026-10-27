using Game.BoluoApi;
using UnityEditor;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    /// <summary>
    /// Editor 游客远端换票 + API 环境快捷菜单。
    /// 登录入口：<c>GameMain/Boluo/编辑器登录</c>（游客 / 手机 二选一）。
    /// </summary>
    public static class GuestLoginEditorMenu
    {
        const string TryRemoteMenu = "GameMain/Boluo/Editor Guest Login/Try Remote TouristLogin";
        const string EnvDev = "GameMain/Boluo/Editor Guest Login/API Env/dev";
        const string EnvTest = "GameMain/Boluo/Editor Guest Login/API Env/test";
        const string EnvPrerelease = "GameMain/Boluo/Editor Guest Login/API Env/prerelease";
        const string EnvProduct = "GameMain/Boluo/Editor Guest Login/API Env/product";

        [MenuItem(TryRemoteMenu)]
        static void ToggleTryRemote()
        {
            bool next = !EditorPrefs.GetBool(GuestLoginClient.EditorTryRemoteKey, false);
            EditorPrefs.SetBool(GuestLoginClient.EditorTryRemoteKey, next);
            Log.Info("[GuestLogin] Try remote touristLogin on guest proceed = " + next);
        }

        [MenuItem(TryRemoteMenu, true)]
        static bool ToggleTryRemoteValidate()
        {
            Menu.SetChecked(TryRemoteMenu, EditorPrefs.GetBool(GuestLoginClient.EditorTryRemoteKey, false));
            return true;
        }

        [MenuItem(EnvDev)]
        static void SetEnvDev() => SetEnv("dev");

        [MenuItem(EnvTest)]
        static void SetEnvTest() => SetEnv("test");

        [MenuItem(EnvPrerelease)]
        static void SetEnvPrerelease() => SetEnv("prerelease");

        [MenuItem(EnvProduct)]
        static void SetEnvProduct() => SetEnv("product");

        [MenuItem(EnvDev, true)]
        static bool SetEnvDevValidate() => ValidateEnv("dev", EnvDev);

        [MenuItem(EnvTest, true)]
        static bool SetEnvTestValidate() => ValidateEnv("test", EnvTest);

        [MenuItem(EnvPrerelease, true)]
        static bool SetEnvPrereleaseValidate() => ValidateEnv("prerelease", EnvPrerelease);

        [MenuItem(EnvProduct, true)]
        static bool SetEnvProductValidate() => ValidateEnv("product", EnvProduct);

        [MenuItem("GameMain/Boluo/Editor Guest Login/Clear Saved Guest Session")]
        static void ClearSession()
        {
            GuestSession.Clear();
            BoluoSession.ClearDeviceToken();
            Log.Info("[GuestLogin] cleared guest + deviceToken session");
        }

        static void SetEnv(string env)
        {
            BoluoApiEnvironment.SetEditorOverride(env);
            EditorPrefs.SetString(GuestLoginClient.ApiEnvKey, env);
            SignatureLib.ApplyEditorEnvironmentOverride(env);
            BoluoApiEnvironment.ClearCached();
            Log.Info("[GuestLogin] API env = " + env);
        }

        static bool ValidateEnv(string env, string menu)
        {
            string cur = BoluoApiEnvironment.GetEditorOverride();
            if (string.IsNullOrEmpty(cur))
                cur = EditorPrefs.GetString(GuestLoginClient.ApiEnvKey, "dev");
            Menu.SetChecked(menu, cur == env);
            return true;
        }
    }
}
