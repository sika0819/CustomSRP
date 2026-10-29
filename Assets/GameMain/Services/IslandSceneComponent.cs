using System.Collections.Generic;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 岛上灯、地形、海面和天空的登记。挂在 GameFramework 预制体上，
    /// <see cref="GameFrameworkComponent.Awake"/> 注册进框架名单。
    /// 物体在自己的 OnEnable 写入，调用方读这里，不扫场景。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Framework/Island Scene")]
    public sealed class IslandSceneComponent : GameFrameworkComponent
    {
        struct LightRegistration
        {
            public Light Light;
            public bool Keep;
        }

        readonly List<LightRegistration> _lights = new List<LightRegistration>(4);
        Component _skybox;
        IAtmosphereVisualDriver _driver;
        IOilWarpVisualDriver _oilWarp;
        Terrain _terrain;
        ISeaLevel _sea;

        public static IslandSceneComponent Active
        {
            get
            {
                // 框架名单里可能还留着已销毁的组件。?. 不认 Unity 的假 null，会把地形登记写丢。
                IslandSceneComponent scene =
                    UnityGameFramework.Runtime.GameEntry.GetComponent<IslandSceneComponent>();
                if (scene == null)
                    scene = GameEntry.IslandScene;
                return scene == null ? null : scene;
            }
        }

        public Component Skybox
        {
            get
            {
                if (_skybox == null)
                    _skybox = null;
                return _skybox;
            }
        }

        public IAtmosphereVisualDriver Driver
        {
            get
            {
                if (_driver is Object unityObject && unityObject == null)
                    _driver = null;
                return _driver;
            }
        }

        public IOilWarpVisualDriver OilWarp
        {
            get
            {
                if (_oilWarp is Object unityObject && unityObject == null)
                    _oilWarp = null;
                return _oilWarp;
            }
        }

        public Terrain Terrain
        {
            get
            {
                if (_terrain == null)
                    _terrain = null;
                return _terrain;
            }
        }

        public ISeaLevel Sea
        {
            get
            {
                if (_sea is Object unityObject && unityObject == null)
                    _sea = null;
                return _sea;
            }
        }

        public void RegisterSkybox(Component skybox)
        {
            if (skybox != null)
                _skybox = skybox;
        }

        public void UnregisterSkybox(Component skybox)
        {
            if (_skybox == skybox)
                _skybox = null;
        }

        public void RegisterDriver(IAtmosphereVisualDriver driver)
        {
            if (driver != null)
                _driver = driver;
        }

        public void UnregisterDriver(IAtmosphereVisualDriver driver)
        {
            if (_driver == driver)
                _driver = null;
        }

        public void RegisterOilWarp(IOilWarpVisualDriver driver)
        {
            if (driver != null)
                _oilWarp = driver;
        }

        public void UnregisterOilWarp(IOilWarpVisualDriver driver)
        {
            if (_oilWarp == driver)
                _oilWarp = null;
        }

        public void RegisterTerrain(Terrain terrain)
        {
            if (terrain != null)
                _terrain = terrain;
        }

        public void UnregisterTerrain(Terrain terrain)
        {
            if (_terrain == terrain)
                _terrain = null;
        }

        public void RegisterSea(ISeaLevel sea)
        {
            if (sea == null)
                return;
            if (_sea == null || sea.GridScale > _sea.GridScale)
                _sea = sea;
        }

        public void UnregisterSea(ISeaLevel sea)
        {
            if (_sea == sea)
                _sea = null;
        }

        /// <summary>
        /// 同物体上的灯自己登记。<paramref name="keep"/> 为太阳，不会被关掉。
        /// 已登记的灯只会把保留标记升上去，不会降下来。
        /// </summary>
        public void RegisterLight(Light light, bool keep)
        {
            if (light == null)
                return;

            for (int i = 0; i < _lights.Count; i++)
            {
                if (_lights[i].Light != light)
                    continue;
                if (keep && !_lights[i].Keep)
                    _lights[i] = new LightRegistration { Light = light, Keep = true };
                return;
            }

            _lights.Add(new LightRegistration { Light = light, Keep = keep });
        }

        public void UnregisterLight(Light light)
        {
            for (int i = _lights.Count - 1; i >= 0; i--)
            {
                if (_lights[i].Light == light)
                    _lights.RemoveAt(i);
            }
        }

        public void CopyLights(List<Light> results)
        {
            results.Clear();
            for (int i = _lights.Count - 1; i >= 0; i--)
            {
                Light light = _lights[i].Light;
                if (light == null)
                {
                    _lights.RemoveAt(i);
                    continue;
                }

                results.Add(light);
            }
        }

        public void DisableDirectionalsExceptSun()
        {
            for (int i = _lights.Count - 1; i >= 0; i--)
            {
                Light light = _lights[i].Light;
                if (light == null)
                {
                    _lights.RemoveAt(i);
                    continue;
                }

                if (_lights[i].Keep || light.type != LightType.Directional)
                    continue;
                light.enabled = false;
            }
        }

        public void ClearAtmosphere()
        {
            _skybox = null;
            _driver = null;
        }

        public void Clear()
        {
            ClearAtmosphere();
            _oilWarp = null;
            _terrain = null;
            _sea = null;
            _lights.Clear();
        }
    }
}
