using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Server-backed multiplayer performance dashboard, trends and match history.</summary>
    public sealed class PerformanceScreen : InfoListScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Performance;
        public override string Title => "Performance";
        protected override string Banner => "Statistics come from server-validated multiplayer match records only. Local/offline sessions are excluded.";
        protected override bool HasFooter => true;
        protected override float FooterHeight => 68f;

        private static readonly string[] ModeIds = { "", GameModeIds.BattleRoyale, GameModeIds.LoneWolf, GameModeIds.Training };
        private static readonly string[] ModeLabels = { "ALL", "BR", "LONE", "TRAIN" };

        private int modeIndex;
        private bool recentOnly = true;
        private bool loading;
        private string error;
        private PerformanceSummaryEnvelopeDto summary;
        private PerformanceHistoryDto history;
        private PerformanceTrendsDto trends;
        private PerformanceMatchDto detail;
        private string detailError;
        private ButtonView modeButton, scopeButton, refreshButton;

        protected override void OnShown()
        {
            base.OnShown();
            Reload();
        }

        protected override void BuildFooter(RectTransform footer)
        {
            var row = UIKit.NewRect("Controls", footer);
            UIKit.Stretch(row);
            UIKit.HStack(row.gameObject, 8f);

            modeButton = UIKit.NewButton(row, "Mode", ModeLabel(), UITheme.Button, NextMode, UITheme.FontCaption + 2);
            UIKit.SetLayout(modeButton.gameObject, preferredWidth: 170f, preferredHeight: 60f);
            scopeButton = UIKit.NewButton(row, "Scope", ScopeLabel(), UITheme.Button, ToggleScope, UITheme.FontCaption + 2);
            UIKit.SetLayout(scopeButton.gameObject, preferredWidth: 220f, preferredHeight: 60f);
            refreshButton = UIKit.NewButton(row, "Reload", loading ? "LOADING..." : "REFRESH", loading ? UITheme.Disabled : UITheme.Accent, Reload, UITheme.FontCaption + 2);
            refreshButton.SetInteractable(!loading);
            UIKit.SetLayout(refreshButton.gameObject, preferredWidth: 180f, preferredHeight: 60f);
            RefreshFooter();
        }

        protected override IEnumerable<RowData> GetRows()
        {
            var eco = AppServices.Economy;
            if (!eco.IsAvailable) { yield return EconomyUi.Notice("Sign in required", eco.UnavailableReason, UITheme.Warning); yield break; }
            if (error != null) { yield return EconomyUi.Notice("Couldn't load statistics", error, UITheme.Danger); yield break; }
            if (loading || summary == null || history == null || trends == null) { yield return EconomyUi.Notice("Loading...", "", UITheme.TextDim); yield break; }

            var s = summary.summary ?? new PerformanceSummaryDto();
            yield return new RowData { Title = "Matches / Wins / Win rate", Subtitle = $"{s.totalMatches:N0} matches   -   {s.totalWins:N0} wins", RightText = (s.winRate * 100f).ToString("0.0") + "%", RightColor = UITheme.Success };
            yield return new RowData { Title = "K/D and combat output", Subtitle = $"{s.totalKills:N0} kills   -   {s.totalDeaths:N0} deaths   -   {s.totalAssists:N0} assists", RightText = s.kd.ToString("0.00"), RightColor = UITheme.Accent };
            yield return new RowData { Title = "Damage / placement / survival", Subtitle = "Avg damage " + s.avgDamage.ToString("0") + "   -   Avg placement " + s.avgPlacement.ToString("0.0"), RightText = Seconds(s.avgSurvivalSeconds), RightColor = UITheme.Cyan };
            yield return new RowData { Title = "Accuracy / headshots", Subtitle = $"{s.headshots:N0} headshots total   -   Booyahs: {s.totalBooyahs:N0}", RightText = (s.accuracy * 100f).ToString("0.0") + "%", RightColor = UITheme.Cyan };

            if (s.bestMatch != null && !string.IsNullOrEmpty(s.bestMatch.matchId))
            {
                var b = s.bestMatch;
                yield return new RowData
                {
                    Title = "Best match (" + b.mode.Replace('_', ' ').ToUpperInvariant() + ")",
                    Subtitle = "#" + b.placement + "   " + b.kills + "/" + b.deaths + "/" + b.assists + "   Damage " + b.damageDealt + "   " + EconomyUi.FormatTime(b.createdAt),
                    RightText = b.booyah ? "BOOYAH" : "",
                    RightColor = UITheme.Success
                };
            }

            yield return EconomyUi.Notice("Trend (recent matches)", "Kills, damage, win rate and survival over time.", UITheme.TextDim);
            if (trends.trends == null || trends.trends.Count == 0)
            {
                yield return EconomyUi.Notice("No trend data", "Play more multiplayer matches to populate trend charts.", UITheme.TextDim);
            }
            else
            {
                int maxKills = 1, maxDamage = 1, maxSurvival = 1;
                foreach (var p in trends.trends)
                {
                    if (p.kills > maxKills) maxKills = p.kills;
                    if (p.damageDealt > maxDamage) maxDamage = p.damageDealt;
                    if (p.survivalSeconds > maxSurvival) maxSurvival = p.survivalSeconds;
                }
                foreach (var p in trends.trends)
                {
                    yield return new RowData
                    {
                        Title = $"{EconomyUi.FormatTime(p.at)}  #{p.placement}",
                        Subtitle = $"Kills {p.kills}  |  Damage {p.damageDealt}  |  Survival {Seconds(p.survivalSeconds)}  |  Win rate {(p.winRate * 100f):0.#}%",
                        RightText = p.win ? "WIN" : "",
                        RightColor = p.win ? UITheme.Success : UITheme.TextDim,
                        Progress = Math.Max((float)p.kills / maxKills, Math.Max((float)p.damageDealt / maxDamage, (float)p.survivalSeconds / maxSurvival))
                    };
                }
            }

            yield return EconomyUi.Notice("Match history", "Select DETAIL to inspect one match.", UITheme.TextDim);
            if (history.matches == null || history.matches.Count == 0)
            {
                yield return EconomyUi.Notice("No multiplayer history yet", "Complete server-verified multiplayer matches first.", UITheme.TextDim);
            }
            else
            {
                foreach (var m in history.matches)
                {
                    var row = m;
                    yield return new RowData
                    {
                        Title = row.mode.Replace('_', ' ').ToUpperInvariant() + "  -  #" + row.placement,
                        Subtitle = EconomyUi.FormatTime(row.createdAt) + "  -  " + Seconds(row.durationSeconds) + "  -  " + row.kills + "/" + row.deaths + "/" + row.assists + "  -  Dmg " + row.damageDealt,
                        RightText = (row.accuracy * 100f).ToString("0.#") + "%",
                        RightColor = UITheme.Cyan,
                        ActionLabel = "DETAIL",
                        ActionEnabled = !loading,
                        Action = () => LoadDetail(row.matchId),
                    };
                }
            }

            if (detail != null)
            {
                yield return EconomyUi.Notice("Selected match: " + detail.matchId, $"{detail.mode.Replace('_', ' ')}  -  {EconomyUi.FormatTime(detail.createdAt)}", UITheme.Accent);
                yield return new RowData { Title = "Placement / duration", Subtitle = "#" + detail.placement + " of " + detail.playerCount + " players", RightText = Seconds(detail.durationSeconds), RightColor = UITheme.Text };
                yield return new RowData { Title = "K / D / A", Subtitle = detail.kills + " / " + detail.deaths + " / " + detail.assists, RightText = "HS " + detail.headshots, RightColor = UITheme.Accent };
                yield return new RowData { Title = "Damage dealt / taken", Subtitle = detail.damageDealt + " / " + detail.damageTaken, RightText = "RP " + Signed(detail.rankPointsDelta), RightColor = detail.rankPointsDelta >= 0 ? UITheme.Success : UITheme.Warning };
                yield return new RowData { Title = "Accuracy / headshot rate", Subtitle = (detail.accuracy * 100f).ToString("0.#") + "% accuracy", RightText = (detail.headshotRate * 100f).ToString("0.#") + "%", RightColor = UITheme.Cyan };
                yield return new RowData { Title = "Rewards", Subtitle = "+" + detail.rewardsCoins + " coins   +" + detail.rewardsDiamonds + " diamonds", RightText = detail.booyah ? "BOOYAH" : "", RightColor = UITheme.Success };
            }
            else if (detailError != null)
            {
                yield return EconomyUi.Notice("Could not load selected match", detailError, UITheme.Warning);
            }
        }

        private void NextMode()
        {
            modeIndex = (modeIndex + 1) % ModeIds.Length;
            Reload();
        }

        private void ToggleScope()
        {
            recentOnly = !recentOnly;
            Reload();
        }

        private void Reload()
        {
            var eco = AppServices.Economy;
            if (!eco.IsAvailable) { Rebuild(); return; }
            loading = true; error = null; detailError = null; detail = null;
            string mode = ModeIds[modeIndex];
            int remaining = 3;

            void Complete(string e = null)
            {
                if (e != null && error == null) error = e;
                remaining--;
                if (remaining > 0) return;
                loading = false;
                RefreshFooter();
                Rebuild();
            }

            eco.LoadPerformanceSummary(mode, recentOnly, recentOnly ? 30 : 100, r =>
            {
                if (r.Ok) summary = r.Value;
                Complete(r.Ok ? null : r.Friendly);
            });
            eco.LoadPerformanceHistory(mode, recentOnly ? 20 : 50, 0, r =>
            {
                if (r.Ok) history = r.Value;
                Complete(r.Ok ? null : r.Friendly);
            });
            eco.LoadPerformanceTrends(mode, recentOnly ? 20 : 40, r =>
            {
                if (r.Ok) trends = r.Value;
                Complete(r.Ok ? null : r.Friendly);
            });
            RefreshFooter();
            Rebuild();
        }

        private void LoadDetail(string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return;
            detailError = null;
            AppServices.Economy.LoadPerformanceMatch(matchId, r =>
            {
                if (r.Ok) detail = r.Value;
                else detailError = r.Friendly;
                Rebuild();
            });
        }

        private string ModeLabel() => "MODE: " + ModeLabels[Mathf.Clamp(modeIndex, 0, ModeLabels.Length - 1)];
        private string ScopeLabel() => recentOnly ? "SCOPE: RECENT" : "SCOPE: ALL-TIME";
        private void RefreshFooter()
        {
            if (modeButton != null) modeButton.SetLabel(ModeLabel());
            if (scopeButton != null) scopeButton.SetLabel(ScopeLabel());
            if (refreshButton != null)
            {
                refreshButton.SetLabel(loading ? "LOADING..." : "REFRESH");
                refreshButton.SetColor(loading ? UITheme.Disabled : UITheme.Accent);
                refreshButton.SetInteractable(!loading);
            }
        }
        private static string Signed(int v) => v >= 0 ? "+" + v : v.ToString();
        private static string Seconds(float s) => Mathf.RoundToInt(s) + "s";
    }
}
