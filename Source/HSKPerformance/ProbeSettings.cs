using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>Options > Mod settings > HSKPerformance. Values are stored in the same config.txt the recorder reads on every start.</summary>
    public sealed class HSKPerformanceMod : Mod
    {
        ProbeConfig cfg;
        Vector2 scroll;
        string durationBuf, warmupBuf;
        bool sRot, sMap, sEff, sMoth; float sSpeed; // what was last applied, to notice a click

        public HSKPerformanceMod(ModContentPack content) : base(content) { }

        public override string SettingsCategory() { return "HSK Performance"; }

        void Ensure()
        {
            if (cfg != null) return;
            cfg = ProbeConfig.Load();
            durationBuf = cfg.DurationSeconds.ToString();
            warmupBuf = cfg.WarmupSeconds.ToString();
            sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sSpeed = cfg.PawnEffectsMinSpeed;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Ensure();
            var view = new Rect(0f, 0f, inRect.width - 20f, 1180f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var l = new Listing_Standard();
            l.Begin(view);

            l.Label("Запись");
            string state = ProbeSession.State == SessionState.Idle ? "не идёт" : ProbeSession.State.ToString();
            l.Label("Состояние: " + state + ". Горячие клавиши: Ctrl+F9 запись, Ctrl+F10 списки тикающего, Ctrl+F8 лёгкий режим вкл/выкл.");
            if (l.ButtonText(ProbeSession.State == SessionState.Idle ? "Начать запись (окно закроется)" : "Остановить запись"))
            {
                cfg.Save();
                if (ProbeSession.State == SessionState.Idle) ProbeSession.Start(); else ProbeSession.Stop("settings button");
                Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), true);
            }
            l.Gap(6f);

            l.CheckboxLabeled("Две записи подряд: сначала лёгкая, потом полная (одним нажатием)", ref cfg.TwoPhase,
                "Ctrl+F9 запускает лёгкую запись, по её окончании сама начинается полная. Отчёты сохраняются в две папки: report-...-light и report-...-full. Ручная остановка отменяет вторую половину.");
            l.CheckboxLabeled("Лёгкий режим для одиночной записи (корни и методы кадра, почти без нагрузки)", ref cfg.LightMode,
                "Показывает реальную долю рендера и ожидания видеокарты. Моды не хукаются, таблицы по модам будут пустыми.");
            l.Label("Длительность каждой записи, секунд");
            l.TextFieldNumeric(ref cfg.DurationSeconds, ref durationBuf, 5f, 600f);
            l.Label("Прогрев перед записью, секунд");
            l.TextFieldNumeric(ref cfg.WarmupSeconds, ref warmupBuf, 0f, 60f);

            l.Gap(8f);
            l.Label("Что хукать (в лёгком режиме не действует)");
            l.CheckboxLabeled("Тики модов", ref cfg.HookTick);
            l.CheckboxLabeled("Update модов", ref cfg.HookUpdate);
            l.CheckboxLabeled("Интерфейс модов (OnGUI)", ref cfg.HookGui);
            l.CheckboxLabeled("Патчи Harmony других модов", ref cfg.HookHarmony);
            l.CheckboxLabeled("Части ванильного тика", ref cfg.HookVanillaInternals);
            l.CheckboxLabeled("Трекеры пешки (много вызовов, больше нагрузки)", ref cfg.HookPawnTrackers);

            l.Gap(8f);
            l.Label("Исправления производительности (действуют сразу, перезапуск не нужен)");
            l.CheckboxLabeled("Быстрый поиск склада для гниения (HSK Core)", ref cfg.FixRotStorage,
                "Заменяет медленный перебор всех хранилищ в CompBetterRottable_Patch на одну проверку клетки. Ответ проверяется по оригиналу на первых 1000 вызовах.");
            l.CheckboxLabeled("Без косметических эффектов на высокой скорости: следы пешек, дыхание, рябь, дым разбитых капсул и кораблей", ref cfg.FixPawnEffects,
                "Не создаются: следы и пар от дыхания пешек, рябь на воде и чёрный дым разбитых капсул и кораблей HSK Core. Дым печей и электростанций, огонь костров и пожары остаются как в ваниле. Работает на выбранной скорости и быстрее, на игру не влияет, только на вид.");
            if (cfg.FixPawnEffects)
            {
                l.Label("Отключать эффекты, начиная со скорости:");
                if (l.RadioButton("Вторая (Fast)", cfg.PawnEffectsMinSpeed <= 3f, 24f)) cfg.PawnEffectsMinSpeed = 3f;
                if (l.RadioButton("Третья (Superfast)", cfg.PawnEffectsMinSpeed > 3f && cfg.PawnEffectsMinSpeed <= 6f, 24f)) cfg.PawnEffectsMinSpeed = 6f;
                if (l.RadioButton("Четвёртая (Ultrafast)", cfg.PawnEffectsMinSpeed > 6f, 24f)) cfg.PawnEffectsMinSpeed = 15f;
            }
            l.CheckboxLabeled("Дальняя карта: на максимальном отдалении рисовать только стены, горы и пешек", ref cfg.FixFarMap,
                "Когда клетка на экране меньше порога, игра не рисует землю, здания, растения, предметы, свет, туман, крыши и зоны. Вместо них один фон, стены одним цветом, горы другим. Пешки, животные и враги рисуются как обычно (точки Camera+). Заметно снижает нагрузку при сильном отдалении.");
            if (cfg.FixFarMap)
            {
                l.Label("Включать, когда клетка на экране меньше " + cfg.FarMapPx.ToString("F0") + " пикселей. Больше число: режим включается при более близком зуме. Сейчас клетка " + FarMap.CurrentPx.ToString("F0") + " px (у вас от 9 при максимальном отдалении до 49 вплотную).");
                cfg.FarMapPx = l.Slider(cfg.FarMapPx, 4f, 60f);
                FarMap.ThresholdPx = cfg.FarMapPx;
                l.Label("Цвета дальней карты (RRGGBB): стены, горы, фон, вода, болото. Тёмные цвета лучше выделяют точки пешек.");
                string w0 = cfg.FarMapWall, r0 = cfg.FarMapRock, b0 = cfg.FarMapBackground, wa0 = cfg.FarMapWater, m0 = cfg.FarMapMarsh;
                cfg.FarMapWall = l.TextEntryLabeled("Стены", cfg.FarMapWall);
                cfg.FarMapRock = l.TextEntryLabeled("Горы", cfg.FarMapRock);
                cfg.FarMapBackground = l.TextEntryLabeled("Фон", cfg.FarMapBackground);
                cfg.FarMapWater = l.TextEntryLabeled("Вода", cfg.FarMapWater);
                cfg.FarMapMarsh = l.TextEntryLabeled("Болото", cfg.FarMapMarsh);
                if (cfg.FarMapWall != w0 || cfg.FarMapRock != r0 || cfg.FarMapBackground != b0 || cfg.FarMapWater != wa0 || cfg.FarMapMarsh != m0)
                    FarMap.SetColors(cfg.FarMapWall, cfg.FarMapRock, cfg.FarMapBackground, cfg.FarMapWater, cfg.FarMapMarsh);
            }
            l.CheckboxLabeled("Разрешить замораживание мировых пешек с хроническими болезнями", ref cfg.FixMothballHediffs,
                "Мировые пешки с хроническими болезнями перестают тикать каждый тик. Побочный эффект: эти болезни больше не убивают мировых пешек. Выключение возвращает прежнее поведение при следующей обработке пешек.");
            string liveStatus = PerfFixes.LiveStatus();
            if (liveStatus.Length > 0) l.Label(liveStatus);
            if (cfg.FixRotStorage != sRot || cfg.FixFarMap != sMap || cfg.FixPawnEffects != sEff || cfg.FixMothballHediffs != sMoth || cfg.PawnEffectsMinSpeed != sSpeed)
            {
                sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sSpeed = cfg.PawnEffectsMinSpeed;
                cfg.Save();
                PerfFixes.Reload(cfg);
            }

            l.Gap(8f);
            if (l.ButtonText("Записать списки тикающего сейчас (Ctrl+F10)"))
            {
                string msg = WorldPawnDump.WriteStandalone();
                ProbeSession.Notify(msg ?? "Списки доступны только внутри игры с загруженной картой.");
            }
            l.Label("Файл настроек: " + ProbeConfig.Dir);
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
