using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayFab.PfEditor
{
    public class PlayFabEditor : UnityEditor.EditorWindow
    {
        #region EdEx Variables
        // vars for the plugin-wide event system
        public enum EdExStates { OnEnable, OnDisable, OnLogin, OnLogout, OnMenuItemClicked, OnSubmenuItemClicked, OnHttpReq, OnHttpRes, OnError, OnSuccess, OnWarning, OnDataLoaded } //OnWaitBegin, OnWaitEnd,

        public delegate void PlayFabEdExStateHandler(EdExStates state, string status, string misc);
        public static event PlayFabEdExStateHandler EdExStateUpdate;

        public static Dictionary<string, float> blockingRequests = new Dictionary<string, float>(); // key and blockingRequest start time
        private static float blockingRequestTimeOut = 10f; // abandon the block after this many seconds.

        public static string latestEdExVersion = string.Empty;

        internal static PlayFabEditor window;
        #endregion

        #region unity lopps & methods
        void OnEnable()
        {
            if (window == null)
            {
                window = this;
                window.minSize = new Vector2(320, 0);
            }

            if (!IsEventHandlerRegistered(StateUpdateHandler))
            {
                EdExStateUpdate += StateUpdateHandler;
            }

            RaiseStateUpdate(EdExStates.OnEnable);
            PlayFabEditorDataService.LoadAllData();
            GetLatestEdExVersion();
        }

        void OnDisable()
        {
            // clean up objects:
            PlayFabEditorDataService.EditorSettings.isEdExShown = false;

            if (IsEventHandlerRegistered(StateUpdateHandler))
            {
                EdExStateUpdate -= StateUpdateHandler;
            }
        }

        void OnFocus()
        {
            OnEnable();
        }

        [MenuItem("Window/PlayFab/Editor Extensions")]
        static void PlayFabServices()
        {
            var editorAsm = typeof(UnityEditor.Editor).Assembly;
            var inspWndType = editorAsm.GetType("UnityEditor.SceneHierarchyWindow");

            if (inspWndType == null)
            {
                inspWndType = editorAsm.GetType("UnityEditor.InspectorWindow");
            }

            window = GetWindow<PlayFabEditor>(inspWndType);
            window.titleContent = new GUIContent("PlayFab EdEx");
            PlayFabEditorDataService.EditorSettings.isEdExShown = true;
        }

        [InitializeOnLoad]
        public class Startup
        {
            static Startup()
            {
                PlayFabEditorDataService.LoadAllData();
                if (PlayFabEditorDataService.EditorSettings.isEdExShown || !PlayFabEditorSDKTools.IsInstalled)
                {
                    EditorCoroutine.Start(OpenPlayServices());
                }
            }
        }

        static IEnumerator OpenPlayServices()
        {
            yield return new WaitForSeconds(1f);
            if (!Application.isPlaying)
            {
                PlayFabServices();
            }
        }

        void OnGUI()
        {
            GUI.skin = PlayFabEditorHelper.uiStyle;

            GUILayout.BeginVertical();

            //Run all updaters prior to drawing;  
            PlayFabEditorSettings.Update();
            PlayFabEditorPackageManager.Update();
            PlayFabEditorHeader.DrawHeader();

            GUI.enabled = blockingRequests.Count == 0 && !EditorApplication.isCompiling;

            if (!PlayFabEditorDataService.IsDataLoaded)
                return;

            if (PlayFabEditorAuthenticate.IsAuthenticated())
            {
                PlayFabEditorMenu.DrawMenu();

                switch (PlayFabEditorMenu._menuState)
                {
                    case PlayFabEditorMenu.MenuStates.Sdks:
                        HideRepaintErrors(PlayFabEditorSDKTools.DrawSdkPanel);
                        break;
                    case PlayFabEditorMenu.MenuStates.Settings:
                        HideRepaintErrors(PlayFabEditorSettings.DrawSettingsPanel);
                        PlayFabEditorSettings.After();
                        break;
                    case PlayFabEditorMenu.MenuStates.Help:
                        HideRepaintErrors(PlayFabEditorHelpMenu.DrawHelpPanel);
                        break;
                    case PlayFabEditorMenu.MenuStates.Data:
                        HideRepaintErrors(PlayFabEditorDataMenu.DrawDataPanel);
                        break;
                    case PlayFabEditorMenu.MenuStates.Tools:
                        HideRepaintErrors(PlayFabEditorToolsMenu.DrawToolsPanel);
                        break;
                    default:
                        break;
                }
            }
            else
            {
                PlayFabEditorAuthenticate.DrawAuthPanels();
            }

            GUILayout.BeginVertical(PlayFabEditorHelper.uiStyle.GetStyle("gpStyleGray1"), GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();

            // help tag at the bottom of the help menu.
            if (PlayFabEditorMenu._menuState == PlayFabEditorMenu.MenuStates.Help)
            {
                DisplayHelpMenu();
            }

            GUILayout.EndVertical();

            PruneBlockingRequests();

            HideRepaintErrors(Repaint);
        }

        private static void HideRepaintErrors(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (!e.Message.ToLower().Contains("repaint"))
                    throw;
                // Hide any repaint issues when recompiling
            }
        }

        private static void DisplayHelpMenu()
        {
            GUILayout.BeginVertical(PlayFabEditorHelper.uiStyle.GetStyle("gpStyleGray1"));
            GUILayout.BeginHorizontal(PlayFabEditorHelper.uiStyle.GetStyle("gpStyleClear"));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(string.Format("PlayFab Editor Extensions: {0}", PlayFabEditorHelper.EDEX_VERSION), PlayFabEditorHelper.uiStyle.GetStyle("versionText"));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            //TODO Add plugin upgrade option here (if available);
            if (ShowEdExUpgrade())
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("UPGRADE EDEX", PlayFabEditorHelper.uiStyle.GetStyle("textButtonOr")))
                {
                    UpgradeEdEx();
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("VIEW DOCUMENTATION", PlayFabEditorHelper.uiStyle.GetStyle("textButton")))
            {
                Application.OpenURL("https://github.com/PlayFab/UnityEditorExtensions");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("REPORT ISSUES", PlayFabEditorHelper.uiStyle.GetStyle("textButton")))
            {
                Application.OpenURL("https://github.com/PlayFab/UnityEditorExtensions/issues");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(PlayFabEditorHelper.EDEX_ROOT))
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("UNINSTALL ", PlayFabEditorHelper.uiStyle.GetStyle("textButton")))
                {
                    RemoveEdEx();
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }
        #endregion

        #region menu and helper methods
        public static void RaiseStateUpdate(EdExStates state, string status = null, string json = null)
        {
            if (EdExStateUpdate != null)
                EdExStateUpdate(state, status, json);
        }

        private static void PruneBlockingRequests()
        {
            List<string> itemsToRemove = new List<string>();
            foreach (var req in blockingRequests)
                if (req.Value + blockingRequestTimeOut < (float)EditorApplication.timeSinceStartup)
                    itemsToRemove.Add(req.Key);

            foreach (var item in itemsToRemove)
            {
                ClearBlockingRequest(item);
                RaiseStateUpdate(EdExStates.OnWarning, string.Format(" Request {0} has timed out after {1} seconds.", item, blockingRequestTimeOut));
            }
        }

        private static void AddBlockingRequest(string state)
        {
            blockingRequests[state] = (float)EditorApplication.timeSinceStartup;
        }

        private static void ClearBlockingRequest(string state = null)
        {
            if (state == null)
            {
                blockingRequests.Clear();
            }
            else if (blockingRequests.ContainsKey(state))
            {
                blockingRequests.Remove(state);
            }
        }

        /// <summary>
        /// Handles state updates within the editor extension.
        /// </summary>
        /// <param name="state">the state that triggered this event.</param>
        /// <param name="status">a generic message about the status.</param>
        /// <param name="json">a generic container for additional JSON encoded info.</param>
        private void StateUpdateHandler(EdExStates state, string status, string json)
        {
            switch (state)
            {
                case EdExStates.OnMenuItemClicked:
                    //Debug.Log(string.Format("State: {0} - MenuItem: {1} Clicked", state, status));
                    PlayFabEditorDataService.EditorView.currentSubMenu = 0;
                    break;

                case EdExStates.OnSubmenuItemClicked:
                    //Debug.Log(string.Format("State: {0} - SubMenuItem: {1} Clicked", state, status));
                    int parsed;
                    if (int.TryParse(json, out parsed))
                        PlayFabEditorDataService.EditorView.currentSubMenu = parsed;
                    break;

                case EdExStates.OnHttpReq:
                    object temp;
                    if (string.IsNullOrEmpty(json) || Json.PlayFabSimpleJson.TryDeserializeObject(json, out temp))
                        break;

                    var deserialized = temp as Json.JsonObject;
                    object useSpinner = false;
                    object blockUi = false;

                    if (deserialized.TryGetValue("useSpinner", out useSpinner) && bool.Parse(useSpinner.ToString()))
                    {
                        ProgressBar.UpdateState(ProgressBar.ProgressBarStates.spin);
                    }

                    if (deserialized.TryGetValue("blockUi", out blockUi) && bool.Parse(blockUi.ToString()))
                    {
                        AddBlockingRequest(status);
                    }
                    break;

                case EdExStates.OnHttpRes:
                    ProgressBar.UpdateState(ProgressBar.ProgressBarStates.off);
                    ProgressBar.UpdateState(ProgressBar.ProgressBarStates.success);
                    ClearBlockingRequest(status);
                    break;

                case EdExStates.OnError:
                    // deserialize and add json details
                    // clear blocking requests
                    ProgressBar.UpdateState(ProgressBar.ProgressBarStates.error);
                    ClearBlockingRequest();
                    Debug.LogError(string.Format("PlayFab EditorExtensions: Caught an error:{0}", status));
                    break;

                case EdExStates.OnWarning:
                    ProgressBar.UpdateState(ProgressBar.ProgressBarStates.warning);
                    ClearBlockingRequest();
                    Debug.LogWarning(string.Format("PlayFab EditorExtensions: {0}", status));
                    break;

                case EdExStates.OnSuccess:
                    ClearBlockingRequest();
                    ProgressBar.UpdateState(ProgressBar.ProgressBarStates.success);
                    break;
            }
        }

        public static bool IsEventHandlerRegistered(PlayFabEdExStateHandler prospectiveHandler)
        {
            if (EdExStateUpdate == null)
                return false;

            foreach (PlayFabEdExStateHandler existingHandler in EdExStateUpdate.GetInvocationList())
                if (existingHandler == prospectiveHandler)
                    return true;
            return false;
        }

        private static void GetLatestEdExVersion()
        {
            var threshold = PlayFabEditorDataService.EditorSettings.lastEdExVersionCheck != DateTime.MinValue ? PlayFabEditorDataService.EditorSettings.lastEdExVersionCheck.AddHours(1) : DateTime.MinValue;

            if (DateTime.Today > threshold)
            {
                PlayFabEditorHttp.MakeGitHubApiCall("https://api.github.com/repos/PlayFab/UnityEditorExtensions/git/refs/tags", (version) =>
                {
                    latestEdExVersion = version ?? "Unknown";
                    PlayFabEditorDataService.EditorSettings.latestEdExVersion = latestEdExVersion;
                });
            }
            else
            {
                latestEdExVersion = PlayFabEditorDataService.EditorSettings.latestEdExVersion;
            }
        }

        private static bool ShowEdExUpgrade()
        {
            if (string.IsNullOrEmpty(latestEdExVersion) || latestEdExVersion == "Unknown")
            {
                return false;
            }

            if (string.IsNullOrEmpty(PlayFabEditorHelper.EDEX_VERSION) || PlayFabEditorHelper.EDEX_VERSION == "Unknown")
            {
                return true;
            }

            string[] currrent = PlayFabEditorHelper.EDEX_VERSION.Split('.');
            string[] latest = latestEdExVersion.Split('.');

            return int.Parse(latest[0]) > int.Parse(currrent[0])
                || int.Parse(latest[1]) > int.Parse(currrent[1])
                || int.Parse(latest[2]) > int.Parse(currrent[2]);
        }

        private static void RemoveEdEx(bool clearPrefs = true, bool prompt = true)
        {
            if (prompt && !EditorUtility.DisplayDialog("Confirm Editor Extensions Removal", "This action will remove PlayFab Editor Extensions from the current project.", "Confirm", "Cancel"))
                return;

            try
            {
                window.Close();
                var edExRoot = new DirectoryInfo(PlayFabEditorHelper.EDEX_ROOT);

                FileUtil.DeleteFileOrDirectory(edExRoot.Parent.FullName);

                if (clearPrefs)
                {
                    PlayFabEditorDataService.RemoveEditorPrefs();
                }

                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                RaiseStateUpdate(EdExStates.OnError, ex.Message);
            }
        }

        private static void UpgradeEdEx()
        {
            if (EditorUtility.DisplayDialog("Confirm EdEx Upgrade", "This action will remove the current PlayFab Editor Extensions and install the lastet version.", "Confirm", "Cancel"))
            {
                window.Close();
                ImportLatestEdEx();
            }
        }

        private static void ImportLatestEdEx()
        {
            PlayFabEditorHttp.MakeDownloadCall("https://api.playfab.com/sdks/download/unity-edex-upgrade", (fileName) =>
            {
                AssetDatabase.ImportPackage(fileName, false);
                Debug.Log("PlayFab EdEx Upgrade: Complete");
            });
        }
        #endregion
    }
}
