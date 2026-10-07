using NeoModLoader.api;
using UnityEngine;

namespace WorldfallNml
{
    /// <summary>
    /// Worldfall as a NeoModLoader mod. NeoModLoader compiles this file on the player's machine against the DLLs in
    /// the mod's Assemblies folder, which it loads first: Assemblies/FirstPerson.dll is the mod itself, the same DLL
    /// as a plain install. It then adds this component (on an inactive object), calls OnLoad and activates the object,
    /// so the real mod, FirstPerson.WorldBoxMod, added here, wakes up beside it.
    /// Kept to plain C# so any NeoModLoader's compiler takes it; it is not part of FirstPerson.csproj.
    /// </summary>
    public class WorldfallMod : MonoBehaviour, IMod
    {
        ModDeclare _declare;

        public ModDeclare GetDeclaration() { return _declare; }
        public GameObject GetGameObject() { return gameObject; }
        public string GetUrl() { return "https://worldfall3d.com"; }

        public void OnLoad(ModDeclare pModDecl, GameObject pGameObject)
        {
            _declare = pModDecl;
            // The game starts the DLLs in StreamingAssets/mods before NeoModLoader loads its mods, so a plain install
            // (FirstPerson.dll or Worldfall.dll) already runs by now if there is one. Two copies would draw and patch
            // everything twice. Matched by name: each install loads its own copy of the assembly.
            foreach (MonoBehaviour behaviour in FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour == null || behaviour.GetType().FullName != "FirstPerson.WorldBoxMod") continue;
                Debug.LogWarning("[Worldfall] 已从以下位置启动：" + behaviour.gameObject.name +
                                 ".dll（游戏模组目录），因此不会重复启动 NeoModLoader 副本。请仅保留一种安装方式。");
                return;
            }
            pGameObject.AddComponent<FirstPerson.WorldBoxMod>();
            pGameObject.AddComponent<WorldfallNml.WorldfallHotbar>(); // Minecraft 风格快捷栏/背包扩展
            Debug.Log("[Worldfall] 由 NeoModLoader 启动，目录：" + pModDecl.FolderPath);
        }
    }
}
