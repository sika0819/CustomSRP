using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 账号密码登录本地节流：同号累计 5 次服务端错误后锁定 10 分钟（UTC）。
    /// </summary>
    public static class AccountPasswordLoginThrottle
    {
        public const int MaxServerFailuresBeforeLock = 5;
        public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);

        public const string LockedTip =
            "密码错误次数过多，账号暂时锁定，请10分钟后再试或找回密码";

        public static bool IsLocked(string phoneDigits)
        {
            if (string.IsNullOrEmpty(phoneDigits))
                return false;
            string lockKey = BoluoGamePersistence.AccPwLockKey(phoneDigits);
            string s = PlayerPrefs.GetString(lockKey, "");
            if (string.IsNullOrEmpty(s) || !long.TryParse(s, out long untilUtcTicks))
                return false;
            if (DateTime.UtcNow.Ticks >= untilUtcTicks)
            {
                PlayerPrefs.DeleteKey(lockKey);
                PlayerPrefs.DeleteKey(BoluoGamePersistence.AccPwFailKey(phoneDigits));
                BoluoGamePersistence.PlayerPrefsSave();
                return false;
            }

            return true;
        }

        /// <summary>
        /// 记录一次服务端密码错误（非未注册）。返回 true 表示本次已触发锁定。
        /// </summary>
        public static bool RecordServerFailure(string phoneDigits, out int failCount)
        {
            failCount = 0;
            if (string.IsNullOrEmpty(phoneDigits))
                return false;
            if (IsLocked(phoneDigits))
                return true;

            failCount = PlayerPrefs.GetInt(BoluoGamePersistence.AccPwFailKey(phoneDigits), 0) + 1;
            if (failCount >= MaxServerFailuresBeforeLock)
            {
                PlayerPrefs.DeleteKey(BoluoGamePersistence.AccPwFailKey(phoneDigits));
                PlayerPrefs.SetString(
                    BoluoGamePersistence.AccPwLockKey(phoneDigits),
                    DateTime.UtcNow.Add(LockDuration).Ticks.ToString());
                RegisterPhone(phoneDigits);
                BoluoGamePersistence.PlayerPrefsSave();
                return true;
            }

            PlayerPrefs.SetInt(BoluoGamePersistence.AccPwFailKey(phoneDigits), failCount);
            RegisterPhone(phoneDigits);
            BoluoGamePersistence.PlayerPrefsSave();
            return false;
        }

        static void RegisterPhone(string phoneDigits)
        {
            string reg = PlayerPrefs.GetString(BoluoGamePersistence.Keys.AccPwPhoneRegistry, "") ?? "";
            string entry = phoneDigits + "\n";
            if (reg.IndexOf(entry, StringComparison.Ordinal) >= 0)
                return;
            PlayerPrefs.SetString(BoluoGamePersistence.Keys.AccPwPhoneRegistry, reg + entry);
        }

        /// <summary>任意方式登录成功并进岛前：清除全部本地账号密码节流状态。</summary>
        public static void ClearAll()
        {
            string reg = PlayerPrefs.GetString(BoluoGamePersistence.Keys.AccPwPhoneRegistry, "") ?? "";
            if (!string.IsNullOrEmpty(reg))
            {
                string[] lines = reg.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string d = lines[i].Trim();
                    if (string.IsNullOrEmpty(d))
                        continue;
                    PlayerPrefs.DeleteKey(BoluoGamePersistence.AccPwFailKey(d));
                    PlayerPrefs.DeleteKey(BoluoGamePersistence.AccPwLockKey(d));
                }
            }

            PlayerPrefs.DeleteKey(BoluoGamePersistence.Keys.AccPwPhoneRegistry);
            BoluoGamePersistence.PlayerPrefsSave();
        }
    }
}
