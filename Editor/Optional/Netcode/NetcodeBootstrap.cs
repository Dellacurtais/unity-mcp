using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.Netcode
{
    [InitializeOnLoad]
    internal static class NetcodeBootstrap
    {
        private const string PackageId = "com.unity.netcode.gameobjects";
        private static readonly string[] ManagerTypes = { "Unity.Netcode.NetworkManager" };
        private static readonly string[] ObjectTypes = { "Unity.Netcode.NetworkObject" };
        private static readonly string[] BehaviourTypes = { "Unity.Netcode.NetworkBehaviour" };
        private static readonly IReadOnlyDictionary<string, string[]> Types = new Dictionary<string, string[]>
        {
            ["networkManager"] = ManagerTypes,
            ["networkObject"] = ObjectTypes,
            ["networkBehaviour"] = BehaviourTypes,
        };
        private static readonly IReadOnlyDictionary<string, string[]> ManagerProperties = new Dictionary<string, string[]>
        {
            ["protocolVersion"] = new[] { "NetworkConfig.ProtocolVersion", "m_NetworkConfig.ProtocolVersion", "NetworkConfig.m_ProtocolVersion" },
            ["enableSceneManagement"] = new[] { "NetworkConfig.EnableSceneManagement", "m_NetworkConfig.EnableSceneManagement", "NetworkConfig.m_EnableSceneManagement" },
            ["forceSamePrefabs"] = new[] { "NetworkConfig.ForceSamePrefabs", "m_NetworkConfig.ForceSamePrefabs", "NetworkConfig.m_ForceSamePrefabs" },
            ["connectionApproval"] = new[] { "NetworkConfig.ConnectionApproval", "m_NetworkConfig.ConnectionApproval", "NetworkConfig.m_ConnectionApproval" },
            ["clientConnectionBufferTimeout"] = new[] { "NetworkConfig.ClientConnectionBufferTimeout", "m_NetworkConfig.ClientConnectionBufferTimeout" },
            ["networkIdRecycleDelay"] = new[] { "NetworkConfig.NetworkIdRecycleDelay", "m_NetworkConfig.NetworkIdRecycleDelay" },
        };
        private static readonly IReadOnlyDictionary<string, string[]> ObjectProperties = new Dictionary<string, string[]>
        {
            ["alwaysReplicateAsRoot"] = new[] { "AlwaysReplicateAsRoot", "m_AlwaysReplicateAsRoot" },
            ["synchronizeTransform"] = new[] { "SynchronizeTransform", "m_SynchronizeTransform" },
            ["spawnWithObservers"] = new[] { "SpawnWithObservers", "m_SpawnWithObservers" },
            ["dontDestroyWithOwner"] = new[] { "DontDestroyWithOwner", "m_DontDestroyWithOwner" },
        };

        static NetcodeBootstrap() { EditorApplication.delayCall += Initialize; }
        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("netcode", PackageId,
                    new OptionalContextTool("get_netcode_context", "Reports Netcode for GameObjects package and scene types.", PackageId, Types),
                    new OptionalListComponentsTool("list_netcode_components", "Lists NetworkManager, NetworkObject and NetworkBehaviour components.", PackageId, ManagerTypes, ObjectTypes, BehaviourTypes),
                    new OptionalCreateComponentTool("create_network_manager", "Create NetworkManager", ManagerTypes),
                    new OptionalAddComponentTool("add_network_object", "Add NetworkObject", ObjectTypes),
                    new OptionalConfigureComponentTool("configure_network_manager", "Configure NetworkManager", ManagerTypes, ManagerProperties),
                    new OptionalConfigureComponentTool("configure_network_object", "Configure NetworkObject", ObjectTypes, ObjectProperties),
                    new OptionalValidateTool("validate_netcode_setup", "Validates Netcode for GameObjects scene setup.", PackageId, Types,
                        Tuple.Create("networkBehaviour", "networkObject")));
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
