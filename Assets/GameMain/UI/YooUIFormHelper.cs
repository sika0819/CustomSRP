using GameFramework.UI;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// UI form helper that releases assets through <see cref="YooAssetResourceManager"/>.
    /// </summary>
    public sealed class YooUIFormHelper : UIFormHelperBase
    {
        public override object InstantiateUIForm(object uiFormAsset)
        {
            var prefab = uiFormAsset as GameObject;
            if (prefab == null)
            {
                Log.Error("UI form asset is not a GameObject (got "
                    + (uiFormAsset == null ? "null" : uiFormAsset.GetType().Name) + ").");
                return null;
            }

            return Instantiate(prefab);
        }

        public override IUIForm CreateUIForm(object uiFormInstance, IUIGroup uiGroup, object userData)
        {
            // Unity fake-null: destroyed pooled GO still boxes as GameObject to `as`,
            // but == null is true — classic after BootLoadingService ForceDestroy + pool Spawn.
            GameObject gameObject = uiFormInstance as GameObject;
            if (gameObject == null)
            {
                bool destroyedUnityObject = uiFormInstance is Object unityObj && unityObj == null;
                Log.Error(destroyedUnityObject
                    ? "UI form instance is invalid (destroyed — UI instance pool likely poisoned)."
                    : "UI form instance is invalid (type="
                        + (uiFormInstance == null ? "null" : uiFormInstance.GetType().Name) + ").");
                return null;
            }

            Transform transform = gameObject.transform;
            transform.SetParent(((MonoBehaviour)uiGroup.Helper).transform);
            transform.localScale = Vector3.one;
            return gameObject.GetOrAddComponent<UIForm>();
        }

        public override void ReleaseUIForm(object uiFormAsset, object uiFormInstance)
        {
            YooAssetResourceManager.Instance?.UnloadAsset(uiFormAsset);
            Destroy((Object)uiFormInstance);
        }
    }
}
