using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// In-match banner for server-driven map events. Attach to the match HUD with Create(parent, matchId).
    /// It polls the backend (the dedicated server publishes events there) and renders countdowns from absolute
    /// server timestamps via <see cref="MapEventClock"/>, so every client shows the same remaining time.
    /// Objective markers are exposed through <see cref="Markers"/> for the world/minimap layer to draw.
    /// Progress and rewards are never computed here; only the dedicated server can advance or claim them.
    /// </summary>
    public sealed class MapEventHud : MonoBehaviour
    {
        private const float PollSeconds = 5f;

        private readonly MapEventClock clock = new MapEventClock();
        private readonly List<MapEventDto> events = new List<MapEventDto>();
        private string matchId;
        private float nextPoll;
        private bool polling;
        private Text label;

        public IReadOnlyList<MapEventDto> Events => events;

        /// <summary>Events that should currently show a marker (announced or active).</summary>
        public IEnumerable<MapEventDto> Markers
        {
            get
            {
                long now = NowMs();
                foreach (var e in events)
                {
                    string phase = clock.PhaseAt(e, now);
                    if (phase == "announced" || phase == "active") yield return e;
                }
            }
        }

        public static MapEventHud Create(RectTransform parent, string matchId)
        {
            var rect = UIKit.NewRect("MapEventHud", parent);
            UIKit.Anchor(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(620f, 96f));
            var bg = UIKit.NewRounded(rect, "Bg", new Color(0f, 0f, 0f, 0.55f));
            UIKit.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);
            bg.raycastTarget = false;
            var hud = rect.gameObject.AddComponent<MapEventHud>();
            hud.matchId = matchId;
            hud.label = UIKit.NewText(rect, "", UITheme.FontBody, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Label");
            UIKit.Stretch(hud.label.rectTransform, 12f, 6f, 12f, 6f);
            UIKit.FitText(hud.label, 14);
            hud.label.raycastTarget = false;
            rect.gameObject.SetActive(false);
            return hud;
        }

        private static long NowMs() => (long)(Time.realtimeSinceStartup * 1000f);

        private void Update()
        {
            if (!polling && Time.realtimeSinceStartup >= nextPoll && !string.IsNullOrEmpty(matchId)) Poll();
            Render();
        }

        private void Poll()
        {
            polling = true;
            nextPoll = Time.realtimeSinceStartup + PollSeconds;
            AppServices.Community.LoadMapEvents(matchId, r =>
            {
                polling = false;
                if (!r.Ok) return; // keep showing the last known state; the next poll retries
                clock.Sync(r.Value.serverNow, NowMs());
                events.Clear();
                events.AddRange(r.Value.events);
            });
        }

        private void Render()
        {
            if (label == null || !clock.Synced) return;
            long now = NowMs();
            MapEventDto best = null;
            string bestPhase = null;
            foreach (var e in events)
            {
                string phase = clock.PhaseAt(e, now);
                if (phase != "announced" && phase != "active") continue;
                if (best == null || (phase == "active" && bestPhase != "active")) { best = e; bestPhase = phase; }
            }

            gameObject.SetActive(best != null);
            if (best == null) return;
            string verb = bestPhase == "announced" ? "starts in " : "ends in ";
            label.text = best.title + "  -  " + CommunityText.MapEventPhaseLabel(bestPhase) + "\n" +
                         verb + CommunityText.Countdown(clock.RemainingMs(best, now)) + "  -  reward " + best.reward.coins + " coins (server-verified)";
        }
    }
}
