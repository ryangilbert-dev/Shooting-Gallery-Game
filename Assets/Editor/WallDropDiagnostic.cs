using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using ShootingGallery.Gameplay;
using ShootingGallery.Networking;

/// <summary>
/// Dev-only diagnostic (not part of the game itself): hosts a session, directly calls
/// MatchManager.RegisterTargetHit ten times to simulate a full bar without needing real aiming,
/// and logs whether the divider wall actually drops. Bypasses shooting/aiming entirely so it
/// isolates the RegisterTargetHit -> WallController wiring from hit-detection, which is already
/// confirmed working (targets turn green in normal play). Kept around as a fast regression check
/// for this specific mechanic - this is exactly how the "wall never lowers" bug was found: it
/// isolated the fault down to MatchManager's dividerWall reference reading back null at runtime
/// (a scene rebuilt via the project's 8.3 short path silently corrupting that cross-reference,
/// same root cause hit earlier with PlayerPrefab) rather than a logic bug in the drop/raise code.
///
/// [InitializeOnLoad] + SessionState (not a plain static field + one-time EditorApplication.update
/// subscription) because entering Play Mode triggers its own domain reload by default, which
/// would otherwise silently drop the subscription and hang the process forever with nothing left
/// to call Exit(). SessionState survives that reload (it only resets on a real Editor restart),
/// so the static constructor below re-subscribes every time and picks up where it left off.
/// </summary>
[InitializeOnLoad]
public static class WallDropDiagnostic
{
    private const string ActiveKey = "WallDropDiagnostic.Active";
    private const string StepKey = "WallDropDiagnostic.Step";
    private const string HitsKey = "WallDropDiagnostic.Hits";
    private const string TimerKey = "WallDropDiagnostic.Timer";
    private const string ClientIdKey = "WallDropDiagnostic.ClientId";

    private enum Step
    {
        WaitingForNetworkManager,
        StartingHost,
        WaitingForLane,
        RegisteringHits,
        WaitingForDrop,
        WaitingForRise,
    }

    static WallDropDiagnostic()
    {
        if (SessionState.GetBool(ActiveKey, false))
        {
            EditorApplication.update += Tick;
        }
    }

    [MenuItem("Tools/Shooting Gallery/Diagnostics/Run Wall Drop Diagnostic")]
    public static void Run()
    {
        SessionState.SetBool(ActiveKey, true);
        SessionState.SetInt(StepKey, (int)Step.WaitingForNetworkManager);
        SessionState.SetInt(HitsKey, 0);
        SessionState.SetFloat(TimerKey, 0f);
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying)
        {
            return;
        }

        var step = (Step)SessionState.GetInt(StepKey, 0);
        float timer = SessionState.GetFloat(TimerKey, 0f) + Time.deltaTime;
        SessionState.SetFloat(TimerKey, timer);
        ulong localClientId = (ulong)SessionState.GetInt(ClientIdKey, 0);

        switch (step)
        {
            case Step.WaitingForNetworkManager:
                if (NetworkManager.Singleton != null)
                {
                    Debug.Log("[WallDropDiagnostic] NetworkManager found - starting host.");
                    Advance(Step.StartingHost);
                }
                else if (timer > 15f)
                {
                    Fail("NetworkManager.Singleton never appeared - is PlayModeBootstrap forcing Bootstrap.unity?");
                }
                break;

            case Step.StartingHost:
                if (ConnectionManager.Instance == null)
                {
                    if (timer > 15f)
                    {
                        Fail("ConnectionManager.Instance never appeared.");
                    }
                    break;
                }

                if (!NetworkManager.Singleton.IsListening)
                {
                    // Mirrors MainMenuUI.OnHostClicked exactly (ConnectionManager.StartHost, then
                    // the explicit Arena scene load) rather than calling NetworkManager directly -
                    // MatchManager only exists once Arena is actually loaded.
                    ConnectionManager.Instance.StartHost();
                    NetworkManager.Singleton.SceneManager.LoadScene("Arena", LoadSceneMode.Single);
                }
                else
                {
                    localClientId = NetworkManager.Singleton.LocalClientId;
                    SessionState.SetInt(ClientIdKey, (int)localClientId);
                    Debug.Log($"[WallDropDiagnostic] Host started, localClientId={localClientId}. Waiting for lane assignment.");
                    Advance(Step.WaitingForLane);
                }
                break;

            case Step.WaitingForLane:
                if (MatchManager.Instance != null && MatchManager.Instance.GetLaneForClient(localClientId) != LaneSide.None)
                {
                    Debug.Log($"[WallDropDiagnostic] Assigned lane: {MatchManager.Instance.GetLaneForClient(localClientId)}. Registering 10 hits directly.");
                    Advance(Step.RegisteringHits);
                }
                else if (timer > 20f)
                {
                    Fail("Never got a lane assignment from MatchManager (PlayerAClientId/PlayerBClientId stayed unset).");
                }
                break;

            case Step.RegisteringHits:
                MatchManager.Instance.RegisterTargetHit(localClientId);
                int hits = SessionState.GetInt(HitsKey, 0) + 1;
                SessionState.SetInt(HitsKey, hits);
                Debug.Log($"[WallDropDiagnostic] RegisterTargetHit call #{hits} - PlayerAHitCount={MatchManager.Instance.PlayerAHitCount.Value} PlayerBHitCount={MatchManager.Instance.PlayerBHitCount.Value}");
                if (hits >= 10)
                {
                    var field = typeof(MatchManager).GetField("dividerWall",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    object dividerWallValue = field?.GetValue(MatchManager.Instance);
                    Debug.Log($"[WallDropDiagnostic] MatchManager.dividerWall (reflected) = {(dividerWallValue == null ? "NULL" : dividerWallValue.ToString())}");
                    Advance(Step.WaitingForDrop);
                }
                break;

            case Step.WaitingForDrop:
                var wall = Object.FindFirstObjectByType<WallController>();
                if (wall == null)
                {
                    Fail("No WallController found in the loaded scene at all.");
                    break;
                }

                if (timer > 1.5f)
                {
                    Debug.Log($"[WallDropDiagnostic] After {timer:F2}s: IsDropped={wall.IsDropped.Value}, localPosition={wall.transform.localPosition}");
                    if (wall.IsDropped.Value)
                    {
                        Advance(Step.WaitingForRise);
                    }
                    else if (timer > 6f)
                    {
                        Fail("IsDropped never became true - RegisterTargetHit's threshold or ServerDropForSeconds call isn't firing.");
                    }
                }
                break;

            case Step.WaitingForRise:
                var wall2 = Object.FindFirstObjectByType<WallController>();
                if (timer > 6.5f)
                {
                    Debug.Log($"[WallDropDiagnostic] After drop+{timer:F2}s: IsDropped={wall2.IsDropped.Value}, localPosition={wall2.transform.localPosition}");
                    Debug.Log(wall2.IsDropped.Value == false
                        ? "[WallDropDiagnostic] RESULT: PASS - wall dropped and rose again."
                        : "[WallDropDiagnostic] RESULT: FAIL - wall never rose back up.");
                    Finish();
                }
                break;
        }
    }

    private static void Advance(Step next)
    {
        SessionState.SetInt(StepKey, (int)next);
        SessionState.SetFloat(TimerKey, 0f);
    }

    private static void Fail(string reason)
    {
        Debug.LogError("[WallDropDiagnostic] RESULT: FAIL - " + reason);
        Finish();
    }

    // Batch mode doesn't get an automatic -quit here (play mode is asynchronous, so a -quit on
    // the command line would kill the process before any of this ever runs) - so this diagnostic
    // has to end the process itself once it has an answer.
    private static void Finish()
    {
        SessionState.SetBool(ActiveKey, false);
        EditorApplication.update -= Tick;
        EditorApplication.isPlaying = false;
        EditorApplication.delayCall += () => EditorApplication.Exit(0);
    }
}
