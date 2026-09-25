using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Options > Mod settings > HSK Performance: four switches for the fixes and one slider for the far map.
    /// Everything else (recording length, hook groups, colours, speed thresholds) lives in config.txt, which the recorder and the fixes read.
    /// </summary>
    public sealed class HSKPerformanceMod : Mod
    {
        ProbeConfig cfg;
        Vector2 scroll;
        bool sRot, sMap, sEff, sMoth, sMark; // what was last applied, to notice a click

        public HSKPerformanceMod(ModContentPack content) : base(content) { }

        public override string SettingsCategory() { return "HSK Performance"; }

        void Ensure()
        {
            if (cfg != null) return;
            cfg = ProbeConfig.Load();
            sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sMark = cfg.FixMarkerColors;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Ensure();
            var view = new Rect(0f, 0f, inRect.width - 20f, 380f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var l = new Listing_Standard();
            l.Begin(view);

            l.Label("Исправления (действуют сразу, перезапуск не нужен)");
            l.Gap(4f);
            l.CheckboxLabeled("Быстрый поиск склада для гниения", ref cfg.FixRotStorage,
                "HSK Core искал склад для каждой гниющей вещи перебором всех хранилищ. Теперь это одна проверка клетки.");
            l.CheckboxLabeled("Без косметических эффектов на высокой скорости", ref cfg.FixPawnEffects,
                "Со второй скорости и выше не создаются следы пешек, пар от дыхания, рябь на воде и дым разбитых капсул и кораблей. Дым печей, огонь костров и пожары остаются. На игру не влияет, только на вид.");
            l.CheckboxLabeled("Заморозка мировых пешек с хроническими болезнями", ref cfg.FixMothballHediffs,
                "Такие пешки перестают тикать каждый тик. Побочный эффект: эти болезни больше не убивают мировых пешек.");
            l.CheckboxLabeled("Дальняя карта при большом отдалении", ref cfg.FixFarMap,
                "Вместо обычной карты рисуются стены, горы, вода и болото на одном фоне. Пешки, животные и враги остаются. Сильно снижает нагрузку при отдалении.");
            l.CheckboxLabeled("Маркеры Camera+: цвета по статусу, имя выбранной пешки", ref cfg.FixMarkerColors,
                "Маркеры целиком закрашены по статусу: колонисты зелёные (выбранные, лежачие и в срыве остаются как в Camera+), гости светло-голубые, пленные оранжевые, рабы жёлтые, хищники не из вашей колонии красные. Под маркером выбранной пешки рисуется её имя. Зелёный цвет не действует на колонистов, для которых у вас есть своё правило в Camera+.");
            if (cfg.FixFarMap)
            {
                l.Gap(2f);
                l.Label("Включать, когда клетка меньше " + cfg.FarMapPx.ToString("F0") + " px (сейчас " + FarMap.CurrentPx.ToString("F0") + " px). Больше число: включается на более близком зуме.");
                cfg.FarMapPx = l.Slider(cfg.FarMapPx, 4f, 60f);
                FarMap.ThresholdPx = cfg.FarMapPx;
            }

            if (cfg.FixRotStorage != sRot || cfg.FixFarMap != sMap || cfg.FixPawnEffects != sEff || cfg.FixMothballHediffs != sMoth || cfg.FixMarkerColors != sMark)
            {
                sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sMark = cfg.FixMarkerColors;
                cfg.Save();
                PerfFixes.Reload(cfg);
            }

            l.Gap(14f);
            l.Label("Запись производительности: Ctrl+F9. Остальные параметры (длительность записи, цвета карты, пороги скорости) в config.txt: " + ProbeConfig.Dir);
            l.End();
            Widgets.EndScrollView();
        }

        public override void WriteSettings()
        {
            if (cfg != null) cfg.Save();
            base.WriteSettings();
        }
    }
}
