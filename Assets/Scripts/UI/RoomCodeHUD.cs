using ShootingGallery.Gameplay;
using ShootingGallery.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace ShootingGallery.UI
{
    /// <summary>
    /// Host-only reminder of the Relay room code, shown top-left while still in the bar
    /// (GamePhase.WaitingForPlayers) so whoever's hosting can read it out to a friend without
    /// needing to alt-tab back to a main menu that's already been unloaded - MainMenuUI's own
    /// room code display only exists for the few seconds before the Arena scene loads. Built
    /// entirely from code at runtime, same as LivesHUD/HitTrackerHUD.
    ///
    /// Only ever shows anything for the client that's actually hosting (ConnectionManager.
    /// LastHostJoinCode is only ever set on the machine that called StartHostWithRelayAsync) -
    /// a joining client's copy of this component just never has a code to show. Also blank for a
    /// host that used the direct-IP path (StartHost, no Relay) instead, since there's no code to
    /// read out in that case.
    /// </summary>
    public class RoomCodeHUD : NetworkBehaviour
    {
        [SerializeField] private float leftMargin = 24f;
        [SerializeField] private float topMargin = 24f;

        private Text codeText;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner || !IsHost || ConnectionManager.Instance == null
                || string.IsNullOrEmpty(ConnectionManager.Instance.LastHostJoinCode))
            {
                // Not the host, or hosted without Relay (direct-IP) - nothing to ever show.
                enabled = false;
                return;
            }

            BuildHud();
        }

        private void Update()
        {
            // Also hides once a second player has actually connected (PlayerBClientId set) - the
            // code's job is done at that point, no reason to keep it on screen even though
            // CurrentPhase itself doesn't move to GalleryPhase until both players walk through the
            // gallery door.
            bool shouldShow = MatchManager.Instance != null
                && MatchManager.Instance.CurrentPhase.Value == GamePhase.WaitingForPlayers
                && MatchManager.Instance.PlayerBClientId.Value == ulong.MaxValue;

            if (codeText.gameObject.activeSelf != shouldShow)
            {
                codeText.gameObject.SetActive(shouldShow);
            }
        }

        private void BuildHud()
        {
            var canvasGO = new GameObject("RoomCodeCanvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Same "Scale With Screen Size" fix as every other runtime-built HUD in the project -
            // Constant Pixel Size looks fine in a shrunk Editor Game view but reads tiny at a real
            // build's native resolution.
            var canvasScaler = canvasGO.AddComponent<CanvasScaler>();
            canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasScaler.matchWidthOrHeight = 0.5f;

            var textGO = new GameObject("RoomCodeText", typeof(RectTransform));
            textGO.transform.SetParent(canvasGO.transform, false);
            var textRect = (RectTransform)textGO.transform;
            textRect.anchorMin = textRect.anchorMax = new Vector2(0f, 1f);
            textRect.pivot = new Vector2(0f, 1f);
            textRect.anchoredPosition = new Vector2(leftMargin, -topMargin);
            textRect.sizeDelta = new Vector2(360f, 50f);

            codeText = textGO.AddComponent<Text>();
            codeText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            codeText.alignment = TextAnchor.UpperLeft;
            codeText.fontSize = 24;
            codeText.fontStyle = FontStyle.Bold;
            codeText.color = Color.white;
            // A lone host only gets a hard, Unity-enforced 60 seconds before Relay tears an
            // unjoined allocation down (confirmed Relay server behavior - see NOTES.md), so by the
            // time anyone's actually reading this in the bar, it may already be stale if nobody
            // connected right away. Flagged rather than shown as if it's evergreen.
            codeText.text = "Room Code: " + ConnectionManager.Instance.LastHostJoinCode +
                              "\n(may have expired if unused for ~60s - rehost if a friend can't join)";
        }
    }
}
