using System;
using System.Collections.Generic;

namespace Game
{
    /// <summary>Bank name → Play_/Stop_ events (Boluo ambience catalog).</summary>
    public static class AmbienceEventCatalog
    {
        public readonly struct Binding
        {
            public readonly string BankName;
            public readonly string StopEvent;
            public readonly string[] PlayEvents;

            public Binding(string bankName, string stopEvent, params string[] playEvents)
            {
                BankName = bankName;
                StopEvent = stopEvent;
                PlayEvents = playEvents ?? Array.Empty<string>();
            }
        }

        static readonly Dictionary<string, Binding> Map =
            new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase)
            {
                { "River_ADD", new Binding("River_ADD", "Stop_River", "Play_River_ADD") },
                {
                    "River_Quad",
                    new Binding("River_Quad", "Stop_River",
                        "Play_River_L", "Play_River_Ls", "Play_River_R", "Play_River_Rs")
                },
                { "Ocean_RD", new Binding("Ocean_RD", "Stop_Boluodao_Ocean") },
                {
                    "Ocean_Rising_tide_LR",
                    new Binding("Ocean_Rising_tide_LR", "Stop_Boluodao_Ocean",
                        "Play_Ocean_Rising_tide_L", "Play_Ocean_Rising_tide_R")
                },
                {
                    "Ocean_Ebb_tide_LR",
                    new Binding("Ocean_Ebb_tide_LR", "Stop_Boluodao_Ocean",
                        "Play_Ocean_Ebb_tide_L", "Play_Ocean_Ebb_tide_R")
                },
                {
                    "Ocean_Calm_Quad",
                    new Binding("Ocean_Calm_Quad", "Stop_Boluodao_Ocean",
                        "Play_Ocean_Calm_L", "Play_Ocean_Calm_Ls",
                        "Play_Ocean_Clam_R", "Play_Ocean_Clam_Rs")
                },
                { "Boluodao_Forest", new Binding("Boluodao_Forest", "Stop_Boluodao_Forest", "Play_Boluodao_Forest") },
                {
                    "Boluodao_Grassland",
                    new Binding("Boluodao_Grassland", "Stop_Boluodao_Grassland", "Play_Boluodao_Grassland")
                },
                { "Boluodao_Plateau", new Binding("Boluodao_Plateau", "Stop_Boluodao_Plateau", "Play_Boluodao_Plateau") },
                { "Campfire", new Binding("Campfire", "Stop_Campfire_End", "Play_Campfire_Start_Loop") },
                {
                    "Waterfall_Big_Quad",
                    new Binding("Waterfall_Big_Quad", "Stop_Waterfall_Big",
                        "Play_Waterfall_Big_LsRs", "Play_Waterfall_Big_LR")
                },
                {
                    "Waterfall_Small_Quad",
                    new Binding("Waterfall_Small_Quad", "Stop_Waterfall_Small",
                        "Play_Waterfall_Small_LR", "Play_Waterfall_Small_LsRs")
                },
                { "Lake_Quad", new Binding("Lake_Quad", "Stop_Lake", "Play_Lake_LR", "Play_Lake_LsRs") },
                { "Lake_RD", new Binding("Lake_RD", "Stop_Lake") },
            };

        public static bool TryGet(string bankName, out Binding binding) =>
            Map.TryGetValue(bankName ?? "", out binding);

        /// <summary>Music bank（用于预加载分类）。</summary>
        public static bool IsMusicBank(string bankName)
            => !string.IsNullOrEmpty(bankName) && bankName.StartsWith("Music", StringComparison.OrdinalIgnoreCase);

        /// <summary>Weather bank（风/雨/雨打水面）。</summary>
        public static bool IsWeatherBank(string bankName)
            => string.Equals(bankName, "Wind", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bankName, "Rain", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bankName, "Rain_Water", StringComparison.OrdinalIgnoreCase);

        /// <summary>One-shot RD bank（用于区分持续音和一次性音）。</summary>
        public static bool IsOneShotBank(string bankName)
            => !string.IsNullOrEmpty(bankName) && bankName.EndsWith("_RD", StringComparison.OrdinalIgnoreCase);

        public static string[] ResolvePlayEvents(string bankName, string fallbackPlayEvent)
        {
            if (TryGet(bankName, out var binding) && binding.PlayEvents.Length > 0)
                return binding.PlayEvents;
            if (!string.IsNullOrEmpty(fallbackPlayEvent))
                return new[] { fallbackPlayEvent };
            return Array.Empty<string>();
        }

        public static string ResolveStopEvent(string bankName, string fallbackStopEvent)
        {
            if (TryGet(bankName, out var binding) && !string.IsNullOrEmpty(binding.StopEvent))
                return binding.StopEvent;
            return fallbackStopEvent;
        }
    }
}
