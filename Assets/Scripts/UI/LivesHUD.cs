using ShootingGallery.Gameplay;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace ShootingGallery.UI
{
    /// <summary>
    /// Owner-only floating HUD showing both combatants' remaining lives during a duel - three pips
    /// per side, one dims out per life lost - plus a full-screen win/lose overlay once a match
    /// ends. A placeholder screen-space readout for now; the plan is to eventually tie the lives
    /// themselves to physical objects in the gallery room instead (see NOTES.md), but the
    /// underlying data (MatchManager.PlayerALives/PlayerBLives/DummyLives) is already the real
    /// source of truth either way, so that swap won't touch anything outside this file. Built
    /// entirely from code at runtime, same as HitTrackerHUD.
    ///
    /// The overlay needs no input or dismissal of its own - it just tracks CurrentPhase every
    /// frame, so it appears and disappears automatically in lockstep with
    /// MatchManager.ServerResetMatch sending everyone back to the bar a few seconds later.
    ///
    /// Reads who the opponent even is fresh every frame (a real second player can replace the
    /// practice dummy mid-session - see MatchManager.DespawnDummyIfActive) rather than latching
    /// onto one NetworkVariable at spawn time, so the opponent pips keep tracking the right source
    /// without needing to re-subscribe when that happens.
    /// </summary>
    public class LivesHUD : NetworkBehaviour
    {
        [SerializeField] private Color pipAliveColor = new Color(0.85f, 0.15f, 0.15f);
        [SerializeField] private Color pipLostColor = new Color(1f, 1f, 1f, 0.15f);
        [SerializeField] private float pipSize = 22f;
        [SerializeField] private float pipSpacing = 8f;
        [SerializeField] private float topMargin = 30f;
        [SerializeField] private float rowSpacing = 30f;

        [Header("Win/lose overlay")]
        [SerializeField] private Color winBackgroundColor = new Color(0.1f, 0.45f, 0.15f, 0.8f);
        [SerializeField] private Color loseBackgroundColor = new Color(0.5f, 0.1f, 0.1f, 0.8f);

        private const int MaxLives = 3;

        private Image[] ownPips;
        private Image[] opponentPips;
        private GameObject resultOverlay;
        private Image resultBackground;
        private Text resultTitleText;
        private Text resultSubtitleText;
        private LaneSide subscribedLane = LaneSide.None;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                return;
            }

            BuildHud();
        }

        private void Update()
        {
            if (!IsOwner || MatchManager.Instance == null)
            {
                return;
            }

            if (subscribedLane == LaneSide.None)
            {
                subscribedLane = MatchManager.Instance.GetLaneForClient(OwnerClientId);
                if (subscribedLane == LaneSide.None)
                {
                    return; // Not assigned a lane yet - try again next frame.
                }
            }

            LaneSide opponentLane = subscribedLane == LaneSide.A ? LaneSide.B : LaneSide.A;

            int ownLives = subscribedLane == LaneSide.A
                ? MatchManager.Instance.PlayerALives.Value
                : MatchManager.Instance.PlayerBLives.Value;

            int opponentLives = MatchManager.Instance.DummyLane.Value == opponentLane
                ? MatchManager.Instance.DummyLives.Value
                : (opponentLane == LaneSide.A ? MatchManager.Instance.PlayerALives.Value : MatchManager.Instance.PlayerBLives.Value);

            ApplyPips(ownPips, ownLives);
            ApplyPips(opponentPips, opponentLives);

            bool isMatchOver = MatchManager.Instance.CurrentPhase.Value == GamePhase.MatchOver;
            if (resultOverlay != null && resultOverlay.activeSelf != isMatchOver)
            {
                resultOverlay.SetActive(isMatchOver);
            }

            if (isMatchOver)
            {
                bool won = ownLives > 0;
                resultTitleText.text = won ? "YOU WIN" : "YOU LOSE";
                resultBackground.color = won ? winBackgroundColor : loseBackgroundColor;
            }
        }

        private void ApplyPips(Image[] pips, int livesRemaining)
        {
            if (pips == null)
            {
                return;
            }

            for (int i = 0; i < pips.Length; i++)
            {
                pips[i].color = i < livesRemaining ? pipAliveColor : pipLostColor;
            }
        }

        private void BuildHud()
        {
            var canvasGO = new GameObject("LivesCanvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Default CanvasScaler mode (Constant Pixel Size) renders at a literal pixel size
            // regardless of screen resolution - looks fine in the Editor's small Game view panel,
            // comically tiny (and the full-screen win/lose overlay potentially not even covering
            // the whole screen) at a real build's full native resolution. Scale With Screen Size
            // instead, relative to a 1920x1080 reference so this HUD's existing pixel sizing
            // stays meaningful.
            var canvasScaler = canvasGO.AddComponent<CanvasScaler>();
            canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasScaler.matchWidthOrHeight = 0.5f;

            ownPips = BuildPipRow(canvasGO.transform, "YouLivesRow", topMargin, "You");
            opponentPips = BuildPipRow(canvasGO.transform, "OpponentLivesRow", topMargin + rowSpacing, "Opponent");

            BuildResultOverlay(canvasGO.transform);
        }

        /// <summary>Full-screen win/lose overlay - a dimmed, win/lose-colored backdrop plus a big
        /// centered title and a smaller subtitle. Built hidden; Update() toggles it and fills in
        /// the text/color once MatchManager.CurrentPhase actually reaches MatchOver.</summary>
        private void BuildResultOverlay(Transform parent)
        {
            resultOverlay = new GameObject("MatchResultOverlay", typeof(RectTransform));
            resultOverlay.transform.SetParent(parent, false);
            var overlayRect = (RectTransform)resultOverlay.transform;
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            resultBackground = resultOverlay.AddComponent<Image>();
            resultBackground.sprite = GetWhiteSprite();
            resultBackground.color = loseBackgroundColor;

            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(resultOverlay.transform, false);
            var titleRect = (RectTransform)titleGO.transform;
            titleRect.anchorMin = titleRect.anchorMax = new Vector2(0.5f, 0.5f);
            titleRect.anchoredPosition = new Vector2(0f, 30f);
            titleRect.sizeDelta = new Vector2(1000f, 200f);
            resultTitleText = titleGO.AddComponent<Text>();
            resultTitleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            resultTitleText.alignment = TextAnchor.MiddleCenter;
            resultTitleText.fontSize = 120;
            resultTitleText.fontStyle = FontStyle.Bold;
            resultTitleText.color = Color.white;
            resultTitleText.text = string.Empty;

            var subtitleGO = new GameObject("Subtitle", typeof(RectTransform));
            subtitleGO.transform.SetParent(resultOverlay.transform, false);
            var subtitleRect = (RectTransform)subtitleGO.transform;
            subtitleRect.anchorMin = subtitleRect.anchorMax = new Vector2(0.5f, 0.5f);
            subtitleRect.anchoredPosition = new Vector2(0f, -80f);
            subtitleRect.sizeDelta = new Vector2(700f, 60f);
            resultSubtitleText = subtitleGO.AddComponent<Text>();
            resultSubtitleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            resultSubtitleText.alignment = TextAnchor.MiddleCenter;
            resultSubtitleText.fontSize = 24;
            resultSubtitleText.color = new Color(1f, 1f, 1f, 0.85f);
            resultSubtitleText.text = "Returning to the bar...";

            resultOverlay.SetActive(false);
        }

        /// <summary>One "LABEL:  ● ● ●" row, anchored top-center and stacked yFromTop pixels down
        /// from the top of the screen.</summary>
        private Image[] BuildPipRow(Transform parent, string rowName, float yFromTop, string label)
        {
            var rowGO = new GameObject(rowName, typeof(RectTransform));
            rowGO.transform.SetParent(parent, false);
            var rowRect = (RectTransform)rowGO.transform;
            rowRect.anchorMin = rowRect.anchorMax = new Vector2(0.5f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchoredPosition = new Vector2(0f, -yFromTop);
            rowRect.sizeDelta = new Vector2(260f, pipSize);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(rowGO.transform, false);
            var labelRect = (RectTransform)labelGO.transform;
            labelRect.anchorMin = new Vector2(0f, 0.5f);
            labelRect.anchorMax = new Vector2(0f, 0.5f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.sizeDelta = new Vector2(90f, pipSize);
            labelRect.anchoredPosition = Vector2.zero;
            var labelText = labelGO.AddComponent<Text>();
            labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelText.alignment = TextAnchor.MiddleRight;
            labelText.fontSize = 16;
            labelText.color = Color.white;
            labelText.text = label;

            var pips = new Image[MaxLives];
            for (int i = 0; i < MaxLives; i++)
            {
                var pipGO = new GameObject("Pip" + i, typeof(RectTransform));
                pipGO.transform.SetParent(rowGO.transform, false);
                var pipRect = (RectTransform)pipGO.transform;
                pipRect.anchorMin = new Vector2(0f, 0.5f);
                pipRect.anchorMax = new Vector2(0f, 0.5f);
                pipRect.pivot = new Vector2(0f, 0.5f);
                pipRect.sizeDelta = new Vector2(pipSize, pipSize);
                pipRect.anchoredPosition = new Vector2(100f + i * (pipSize + pipSpacing), 0f);
                var image = pipGO.AddComponent<Image>();
                image.sprite = GetWhiteSprite();
                pips[i] = image;
            }

            return pips;
        }

        // uGUI's Image needs an actual Sprite (not just a texture) to render at all - same trick
        // HitTrackerHUD uses.
        private static Sprite GetWhiteSprite()
        {
            Texture2D texture = Texture2D.whiteTexture;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }
    }
}
