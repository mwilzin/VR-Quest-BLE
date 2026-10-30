
using UnityEditor;
using UnityEngine;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using System.IO;

[InitializeOnLoad]
public class StarterWizardSetup
{
    // SessionState persists across domain reloads within an Editor session.
    // Used to recover batch mode progress after assembly reloads triggered
    // by fixer tasks (e.g. XR Simulator download causes recompilation).
    private const string SK_Active = "StarterWizard_Active";
    private const string SK_NextPhase = "StarterWizard_NextPhase";

    static StarterWizardSetup()
    {
        if (!Application.isBatchMode) return;
        if (!SessionState.GetBool(SK_Active, false)) return;

        int nextPhase = SessionState.GetInt(SK_NextPhase, 0);
        if (nextPhase >= 2)
        {
            SessionState.SetBool(SK_Active, false);
            EditorApplication.update += ExitOnNextUpdate;
            return;
        }

        // A domain reload interrupted the current phase. Advance to the
        // next phase rather than re-running to avoid infinite reload loops.
        _batchPhase = nextPhase;
        _batchStartTime = EditorApplication.timeSinceStartup;
        SessionState.SetInt(SK_NextPhase, nextPhase + 1);

        var target = nextPhase == 0 ? BuildTargetGroup.Android : BuildTargetGroup.Standalone;
        Debug.Log("Applying all project setup tool suggestions: "
            + (nextPhase == 0 ? "Android" : "Standalone"));
        _fixerTask = OVRProjectSetup.FixAllAsync(target);
        EditorApplication.update += BatchUpdate;
    }

    private static void ExitOnNextUpdate()
    {
        EditorApplication.update -= ExitOnNextUpdate;
        EditorApplication.Exit(0);
    }

    [MenuItem("Meta/Tools/Apply All Project Setup Tool Suggestions")]
    public static void AutoApplySetupSteps()
    {
        if (Application.isBatchMode)
        {
            RunBatchMode();
        }
        else
        {
            RunAsync();
        }
    }

    // In batch mode, FixAllAsync hangs on Unity 6000.2+ because:
    // 1. Its internal Task.Run(WaitForCompletion) spin-waits on fixer.Completed
    // 2. Async fix tasks (e.g. XR Sim download) never complete in batch mode
    // 3. Domain reloads triggered by fixer side-effects wipe all static state
    // Fix: fire-and-forget + poll IsCompleted + timeout + domain reload recovery.
    private static System.Threading.Tasks.Task _fixerTask;
    private static int _batchPhase = 0;
    private static double _batchStartTime;
    private const double BatchTimeoutSeconds = 120;

    private static void RunBatchMode()
    {
        SessionState.SetBool(SK_Active, true);
        SessionState.SetInt(SK_NextPhase, 1);

        _batchPhase = 0;
        _batchStartTime = EditorApplication.timeSinceStartup;
        Debug.Log("Applying all project setup tool suggestions: Android");
        _fixerTask = OVRProjectSetup.FixAllAsync(BuildTargetGroup.Android);
        EditorApplication.update += BatchUpdate;
    }

    private static void BatchUpdate()
    {
        bool completed = _fixerTask != null && _fixerTask.IsCompleted;
        bool timedOut = (EditorApplication.timeSinceStartup - _batchStartTime)
            > BatchTimeoutSeconds;

        if (completed || timedOut)
        {
            if (timedOut && !completed)
            {
                string phaseName = _batchPhase == 0 ? "Android" : "Standalone";
                Debug.LogWarning($"[StarterWizardSetup] FixAllAsync ({phaseName}) timed out after {BatchTimeoutSeconds}s; continuing to next phase.");
            }

            if (_batchPhase == 0)
            {
                _batchPhase = 1;
                _batchStartTime = EditorApplication.timeSinceStartup;
                SessionState.SetInt(SK_NextPhase, 2);
                Debug.Log("Applying all project setup tool suggestions: Standalone");
                _fixerTask = OVRProjectSetup.FixAllAsync(BuildTargetGroup.Standalone);
            }
            else
            {
                SessionState.SetBool(SK_Active, false);
                EditorApplication.update -= BatchUpdate;
                EditorApplication.Exit(0);
            }
        }
    }

    private static async void RunAsync()
    {
        Debug.Log("Applying all project setup tool suggestions: Android");
        await OVRProjectSetup.FixAllAsync(BuildTargetGroup.Android);
        Debug.Log("Applying all project setup tool suggestions: Standalone");
        await OVRProjectSetup.FixAllAsync(BuildTargetGroup.Standalone);
        RemoveStarterScript();
    }

    private static void RemoveStarterScript()
    {
        string scriptPath = "Assets/Editor/StarterWizardSetup.cs";
        if (File.Exists(scriptPath))
        {
            File.Delete(scriptPath);
        }
    }
}
