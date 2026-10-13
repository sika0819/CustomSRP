using UnityEditor;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// 对齐 BoluoUnity 顶部 EditorMode：编辑器游客进岛默认走本地会话；
    /// 可选尝试远程 touristLogin（失败仍回退本地游客）。
    /// </summary>
    public static class GuestLoginEditorMenu
    {
        const string TryRemoteMenu = "GameMain/Boluo/Editor Guest Login/Try Remote TouristLogin";
        const string EnvDev = "GameMain/Boluo/Editor Guest Login/API Env/dev";
        const string EnvTest = "GameMain/Boluo/Editor Guest Login/API Env/test";
        const string EnvProduct = "GameMain/Boluo/Editor Guest Login/API Env/product";

        [MenuItem(TryRemoteMenu)]
        static void ToggleTryRemote()
        {
            bool next = !EditorPrefs.GetBool(GuestLoginClient.EditorTryRemoteKey, false);
            EditorPrefs.SetBool(GuestLoginClient.EditorTryRemoteKey, next);
            Log.Info("[GuestLogin] Try remote touristLogin = " + next);
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

        [MenuItem(EnvProduct)]
        static void SetEnvProduct() => SetEnv("product");

        [MenuItem(EnvDev, true)]
        static bool SetEnvDevValidate() => ValidateEnv("dev", EnvDev);

        [MenuItem(EnvTest, true)]
        static bool SetEnvTestValidate() => ValidateEnv("test", EnvTest);

        [MenuItem(EnvProduct, true)]
        static bool SetEnvProductValidate() => ValidateEnv("product", EnvProduct);

        [MenuItem("GameMain/Boluo/Editor Guest Login/Clear Saved Guest Session")]
        static void ClearSession()
        {
            GuestSession.Clear();
            Log.Info("[GuestLogin] cleared saved guest session");
        }

        static void SetEnv(string env)
        {
            EditorPrefs.SetString(GuestLoginClient.ApiEnvKey, env);
            Log.Info("[GuestLogin] API env = " + env);
        }

        static bool ValidateEnv(string env, string menu)
        {
            string cur = EditorPrefs.GetString(GuestLoginClient.ApiEnvKey, "dev");
            Menu.SetChecked(menu, cur == env);
            return true;
        }
    }
}
