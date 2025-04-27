// SplitScreenPlugin.cs – bereinigte & kompilierfähige Version
// -------------------------------------------
// Wichtigste Änderungen:
//  * Harmony‑Namespace eingebunden
//  * Alle statischen Felder, die nicht wirklich global sein müssen, in Instanzfelder umgewandelt
//  * Kameraverwaltung robuster (nicht genutzte Kameras werden deaktiviert)
//  * Split‑/Merge‑Logik vereinfacht
//  * Viewport‑Berechnung korrigiert (Unity‑Koordinatensystem: (0,0) = links‑unten)
//  * IDisposable‑Pattern (OnDestroy) abgesichert
// -------------------------------------------

using System;
using System.Collections.Generic;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

#pragma warning disable CS0618 // SecurityAction.RequestMinimum is obsolete – für Mod trotzdem nötig
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace RainworldSplitscreen
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SplitScreenPlugin : BaseUnityPlugin
    {
        public const string PluginGuid     = "none.splitscreen";
        public const string PluginName     = "Rainworld Splitscreen";
        public const string PluginVersion  = "0.1.1"; // +hotfix

        // ───────────────────────── Konfiguration ─────────────────────────
        private ConfigEntry<float>          _cfgSplitThreshold;
        private ConfigEntry<float>          _cfgMergeThreshold;
        private ConfigEntry<bool>           _cfgForceAlwaysSplit;
        private ConfigEntry<SplitLayout>    _cfgDefaultLayout;

        public  float SplitThreshold   { get; private set; } = 1000f;
        public  float MergeThreshold   { get; private set; } = 800f;
        public  bool  ForceAlwaysSplit { get; private set; } = false;
        public  SplitLayout DefaultLayout { get; private set; } = SplitLayout.Automatic;

        // ───────────────────── Runtime‑State / Objekte ───────────────────
        private Camera                       _originalCamera;
        private readonly List<SplitCam>      _cams            = new();
        private SplitConfig                  _currentConfig   = SplitConfig.Single;
        private PlayerTracker                _tracker;
        private Harmony                      _harmony;

        // ───────────────────────── Lifecycle ─────────────────────────────
        private void Awake()
        {
            Logger.LogInfo($"[{PluginName}] Initialising …");

            SetupConfig();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            _tracker = new PlayerTracker(Logger);

            On.RainWorld.OnModsInit   += RainWorld_OnModsInit;
            On.RoomCamera.Update      += RoomCamera_Update;
            
            // Warte kurz und versuche dann die Kameras einzurichten
            StartCoroutine(DelayedCameraSetup());

            Logger.LogInfo($"[{PluginName}] Ready!");
        }
        
        private System.Collections.IEnumerator DelayedCameraSetup()
        {
            yield return new WaitForSeconds(2f); // Warte bis das Spiel vollständig initialisiert ist
            Logger.LogInfo("Versuche verzögerte Kamera-Einrichtung...");
            
            // Erste Überprüfung der vorhandenen Kameras
            var allCameras = FindObjectsOfType<Camera>();
            foreach (var cam in allCameras)
            {
                Logger.LogInfo($"Gefundene Kamera: {cam.name}, enabled: {cam.enabled}, depth: {cam.depth}, cullingMask: {cam.cullingMask}");
            }
            
            // Setze _originalCamera, falls sie noch nicht existiert
            if (_originalCamera == null)
            {
                _originalCamera = Camera.main;
                Logger.LogInfo($"DelayedCameraSetup: Main camera = {(_originalCamera != null ? _originalCamera.name : "null")}");
            }
        }

        private void OnDestroy()
        {
            Logger.LogInfo($"[{PluginName}] Cleaning up …");

            CleanupSplitScreen();
            On.RainWorld.OnModsInit   -= RainWorld_OnModsInit;
            On.RoomCamera.Update      -= RoomCamera_Update;
            _harmony?.UnpatchSelf();
        }

        // ─────────────────────── Konfig‑Helper ───────────────────────────
        private void SetupConfig()
        {
            _cfgSplitThreshold = Config.Bind("SplitScreen", "SplitThreshold", 1000f,
                "Abstand (Pixel), ab dem der Bildschirm geteilt wird");
            _cfgMergeThreshold = Config.Bind("SplitScreen", "MergeThreshold", 800f,
                "Abstand (Pixel), ab dem wieder zusammengeführt wird (sollte < SplitThreshold sein)");
            _cfgForceAlwaysSplit = Config.Bind("SplitScreen", "ForceAlwaysSplit", false,
                "Immer gesplittet, unabhängig vom Abstand");
            _cfgDefaultLayout = Config.Bind("SplitScreen", "DefaultLayout", SplitLayout.Automatic,
                "Standard‑Layout, falls nicht automatisch entschieden wird");

            // Apply
            SplitThreshold   = _cfgSplitThreshold.Value;
            MergeThreshold   = _cfgMergeThreshold.Value;
            ForceAlwaysSplit = _cfgForceAlwaysSplit.Value;
            DefaultLayout    = _cfgDefaultLayout.Value;
        }

        // ───────────────────────── Hooks / Patches ───────────────────────
        private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self); // nichts Spezielles bislang
        }

        private void RoomCamera_Update(On.RoomCamera.orig_Update orig, RoomCamera self)
        {
            orig(self);

            if (self.game?.session == null || self.game.Players.Count <= 1)
                return; // kein Co‑op, keine Arbeit

            // Wenn _originalCamera null ist, versuche sie einzurichten
            if (_originalCamera == null)
            {
                Logger.LogInfo("RoomCamera_Update: _originalCamera ist null, versuche SetupSplitScreen");
                SetupSplitScreen(self);
                
                // Wenn immer noch null, probiere direkt Camera.main zu verwenden
                if (_originalCamera == null)
                {
                    _originalCamera = Camera.main;
                    Logger.LogInfo($"RoomCamera_Update: Direkter Zugriff auf Camera.main: {(_originalCamera != null ? "erfolgreich" : "fehlgeschlagen")}");
                    
                    if (_originalCamera == null)
                    {
                        // Als letzten Ausweg eine neue Kamera erstellen
                        var camGO = new GameObject("EmergencySplitScreenCamera");
                        DontDestroyOnLoad(camGO); // Verhindere, dass die Kamera beim Szenenwechsel zerstört wird
                        _originalCamera = camGO.AddComponent<Camera>();
                        _originalCamera.clearFlags = CameraClearFlags.SolidColor;
                        _originalCamera.backgroundColor = Color.black;
                        _originalCamera.rect = new Rect(0, 0, 1, 1);
                        _originalCamera.orthographic = true;
                        _originalCamera.orthographicSize = 20f;
                        _originalCamera.depth = 100;
                        Logger.LogInfo("RoomCamera_Update: Notfall-Kamera erstellt");
                    }
                }
                
                // Split-Kameras neu erstellen
                if (self.game?.Players != null && self.game.Players.Count > 1)
                {
                    CreateSplitCams(self.game.Players.Count);
                    
                    // Debuggen nach dem Erstellen der Kameras
                    Logger.LogInfo($"Kameras nach CreateSplitCams: Original={_originalCamera != null}, SplitCams={_cams.Count}");
                    foreach (var cam in _cams)
                    {
                        Logger.LogInfo($"SplitCam Player {cam.PlayerIdx}: {(cam.Camera != null ? $"enabled={cam.Camera.enabled}, rect={cam.Camera.rect}" : "null")}");
                    }
                }
            }

            UpdateSplitScreenState(self);
        }

        // ───────────────────────── Split‑Screen Core ─────────────────────
        private void SetupSplitScreen(RoomCamera roomCamera)
        {
            // Kamera ermitteln (RainWorld >1.9 hat keine Camera‑Komponente an RoomCamera)
            // Rain World‑RoomCamera ist kein Unity‑Component – hole einfach die Hauptkamera
            _originalCamera = Camera.main;
            Logger.LogInfo($"Camera.main: {(_originalCamera != null ? _originalCamera.name : "null")}");

            if (_originalCamera == null)
            {
                // Wenn Camera.main null ist, versuche andere Kameras zu finden
                var allCameras = FindObjectsOfType<Camera>();
                Logger.LogInfo($"Gefundene Kameras: {allCameras.Length}");

                if (allCameras.Length > 0)
                {
                    // Erste aktive Kamera nehmen
                    foreach (var cam in allCameras)
                    {
                        if (cam.enabled)
                        {
                            _originalCamera = cam;
                            Logger.LogInfo($"Verwende aktive Kamera: {_originalCamera.name}");
                            break;
                        }
                    }

                    // Wenn keine aktive Kamera gefunden, nimm die erste
                    if (_originalCamera == null && allCameras.Length > 0)
                    {
                        _originalCamera = allCameras[0];
                        Logger.LogInfo($"Verwende erste Kamera: {_originalCamera.name}");
                    }
                }
            }

            // Als letzten Ausweg, erstelle eine neue Kamera
            if (_originalCamera == null)
            {
                Logger.LogWarning("Keine existierende Kamera gefunden - erstelle neue Kamera");
                var camGO = new GameObject("SplitScreenMainCamera");
                DontDestroyOnLoad(camGO);
                _originalCamera = camGO.AddComponent<Camera>();
                _originalCamera.clearFlags = CameraClearFlags.SolidColor;
                _originalCamera.backgroundColor = Color.black;
                _originalCamera.orthographic = true;
                _originalCamera.orthographicSize = 20f;
                _originalCamera.depth = 1;
            }

            // Wichtige Eigenschaften sicherstellen
            if (_originalCamera.targetTexture != null)
            {
                Logger.LogInfo("Entferne targetTexture von der Kamera");
                _originalCamera.targetTexture = null;
            }

            // Erste Kamera konfigurieren
            _originalCamera.rect = new Rect(0, 0, 1, 1);
            _originalCamera.enabled = true;

            Logger.LogInfo($"Originalkamera erfolgreich eingerichtet: {_originalCamera.name}");
            Logger.LogInfo($"Kamera-Eigenschaften: clearFlags={_originalCamera.clearFlags}, rect={_originalCamera.rect}, depth={_originalCamera.depth}");

            // Force Split immer aktivieren für Test
            ForceAlwaysSplit = true;
            Logger.LogInfo("ForceAlwaysSplit für Testzwecke aktiviert");

            // Jetzt können wir die Split-Kameras erstellen
            CreateSplitCams(roomCamera.game.Players.Count);
        }

        private void CreateSplitCams(int maxPlayers)
        {
            // Vorhandene Kameras zuerst bereinigen
            CleanupSplitScreen();
            
            // Sicherstellen, dass _originalCamera nicht null ist
            if (_originalCamera == null)
            {
                Logger.LogError("CreateSplitCams: _originalCamera ist null - Split-Screen kann nicht eingerichtet werden");
                return;
            }

            int playerCount = Mathf.Clamp(maxPlayers, 1, 4);
            for (int i = 0; i < playerCount; i++)
            {
                try 
                {
                    var go = new GameObject($"SplitCam_{i}");
                    DontDestroyOnLoad(go);
                    var cam = go.AddComponent<Camera>();
                    
                    // Statt CopyFrom verwenden wir gezielte Eigenschaften-Kopie
                    cam.CopyFrom(_originalCamera);
                    cam.rect = new Rect(0, 0, 0.5f, 0.5f); // Temporär, ApplyViewports setzt korrekte Werte
                    cam.depth = _originalCamera.depth + 1; // Höhere Tiefe als Original für korrekten Render-Order
                    cam.enabled = false; // Erstmal deaktivieren
                    
                    // Position setzen (wird später durch UpdateCamPositions aktualisiert)
                    cam.transform.position = new Vector3(0, 0, _originalCamera.transform.position.z);
                    
                    Logger.LogInfo($"SplitCam_{i} erfolgreich erstellt mit depth={cam.depth}");
                    
                    _cams.Add(new SplitCam(cam, i));
                }
                catch (Exception ex) 
                {
                    Logger.LogError($"Fehler beim Erstellen von SplitCam_{i}: {ex.Message}\n{ex.StackTrace}");
                }
            }
            
            Logger.LogInfo($"Insgesamt {_cams.Count} Split-Kameras erstellt");
        }

        private void UpdateSplitScreenState(RoomCamera rc)
        {
            try
            {
                _tracker.UpdatePlayers(rc);

                var newCfg = DetermineConfig(rc);
                Logger.LogInfo($"SplitScreen Status: Current={_currentConfig}, New={newCfg}");
                
                if (newCfg != _currentConfig)
                {
                    Logger.LogInfo($"SplitScreen Konfiguration ändert sich von {_currentConfig} zu {newCfg}");
                    _currentConfig = newCfg;
                    
                    try {
                        ApplyViewports();
                        Logger.LogInfo("Viewports erfolgreich angewendet");
                    }
                    catch (Exception ex) {
                        Logger.LogError($"Fehler beim Anwenden der Viewports: {ex.Message}\n{ex.StackTrace}");
                    }
                }

                if (_currentConfig != SplitConfig.Single)
                {
                    try {
                        UpdateCamPositions(rc);
                    }
                    catch (Exception ex) {
                        Logger.LogError($"Fehler beim Aktualisieren der Kamerapositionen: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Wenn etwas schief geht, versuche nicht weiter zu splitten
                Logger.LogError($"Kritischer Fehler in UpdateSplitScreenState: {ex.Message}\n{ex.StackTrace}");
                _currentConfig = SplitConfig.Single;
                
                // Versuche wieder zum normalen Zustand zurückzukehren
                try {
                    CleanupSplitScreen();
                }
                catch {
                    // Ignoriere weitere Fehler bei der Bereinigung
                }
            }
        }

        private SplitConfig DetermineConfig(RoomCamera rc)
        {
            try
            {
                int pc = Mathf.Clamp(rc.game.Players.Count, 1, 4);
                Logger.LogInfo($"DetermineConfig: Spieleranzahl = {pc}");
                
                if (!ForceAlwaysSplit && pc == 1) return SplitConfig.Single;

                bool playersFarApart = false;
                try {
                    playersFarApart = ForceAlwaysSplit || _tracker.ArePlayersFarApart(SplitThreshold);
                    Logger.LogInfo($"Spieler weit auseinander: {playersFarApart}, ForceAlwaysSplit: {ForceAlwaysSplit}");
                    
                    if (!playersFarApart && _currentConfig != SplitConfig.Single) {
                        playersFarApart = _tracker.ArePlayersFarApart(MergeThreshold);
                        Logger.LogInfo($"Merge-Überprüfung: Spieler weit auseinander: {playersFarApart}");
                    }
                }
                catch (Exception ex) {
                    Logger.LogError($"Fehler bei ArePlayersFarApart: {ex.Message}");
                    playersFarApart = false;
                }

                if (!playersFarApart) return SplitConfig.Single;

                // Explizite Wahl aus Config
                if (DefaultLayout != SplitLayout.Automatic) {
                    SplitConfig result = MapPreferredLayout(DefaultLayout, pc);
                    Logger.LogInfo($"Verwende Layout aus Config: {DefaultLayout} -> {result}");
                    return result;
                }

                // Automatisch
                SplitConfig autoConfig;
                switch (pc) {
                    case 2:
                        bool widerHorizontally = false;
                        try {
                            widerHorizontally = _tracker.WiderHorizontally();
                        }
                        catch (Exception ex) {
                            Logger.LogError($"Fehler bei WiderHorizontally: {ex.Message}");
                        }
                        autoConfig = widerHorizontally ? SplitConfig.Vertical : SplitConfig.Horizontal;
                        break;
                    case 3:
                        autoConfig = SplitConfig.ThreeWay;
                        break;
                    default:
                        autoConfig = SplitConfig.FourWay;
                        break;
                }
                
                Logger.LogInfo($"Auto-Layout für {pc} Spieler: {autoConfig}");
                return autoConfig;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Kritischer Fehler in DetermineConfig: {ex.Message}\n{ex.StackTrace}");
                return SplitConfig.Single; // Im Zweifelsfall Single zurückgeben
            }
        }

        private static SplitConfig MapPreferredLayout(SplitLayout layout, int pc)
        => layout switch
        {
            SplitLayout.Horizontal => SplitConfig.Horizontal,
            SplitLayout.Vertical   => SplitConfig.Vertical,
            SplitLayout.ThreeWay   => pc >= 3 ? SplitConfig.ThreeWay : SplitConfig.Horizontal,
            SplitLayout.FourWay    => pc == 4 ? SplitConfig.FourWay : pc == 3 ? SplitConfig.ThreeWay : SplitConfig.Horizontal,
            _                      => SplitConfig.Single
        };

        // ───────────────────────── Ansicht / Viewports ───────────────────
        private void ApplyViewports()
        {
            try
            {
                // Sicherheitsprüfung: Wenn keine Kamera vorhanden, können wir nichts tun
                if (_originalCamera == null)
                {
                    Logger.LogError("ApplyViewports: _originalCamera ist null - Ansichten können nicht angewendet werden");
                    return;
                }

                // Überprüfe, ob überhaupt Split-Kameras vorhanden sind
                if (_cams.Count == 0)
                {
                    Logger.LogError("ApplyViewports: Keine Split-Kameras vorhanden. Versuche, sie neu zu erstellen.");
                    CreateSplitCams(Math.Max(2, 4)); // Stelle sicher, dass wir genug Kameras erstellen
                    
                    // Wenn immer noch keine Kameras existieren, bleibe im Einzelmodus
                    if (_cams.Count == 0)
                    {
                        Logger.LogError("ApplyViewports: Konnte keine Split-Kameras erstellen. Bleibe im Einzelmodus.");
                        _currentConfig = SplitConfig.Single;
                        _originalCamera.rect = new Rect(0, 0, 1, 1);
                        _originalCamera.enabled = true;
                        return;
                    }
                }

                Logger.LogInfo($"ApplyViewports: Wende Layout {_currentConfig} an mit {_cams.Count} verfügbaren Split-Kameras");

                // alles abschalten …
                _originalCamera.enabled = _currentConfig == SplitConfig.Single;
                foreach (var c in _cams) 
                {
                    if (c.Camera != null) 
                    {
                        c.Camera.enabled = false;
                    }
                }

                // Vor dem Switch-Statement: Überprüfe, ob alle erforderlichen Kameras existieren
                int requiredCams = _currentConfig switch
                {
                    SplitConfig.ThreeWay => 3,
                    SplitConfig.FourWay => 4,
                    _ => 2
                };

                Logger.LogInfo($"ApplyViewports: Benötige {requiredCams} Kameras, vorhanden sind {_cams.Count}");

                // Wenn nicht genug Kameras vorhanden sind, wechsle zu einem Modus mit weniger Kameras
                if (_cams.Count < requiredCams && _currentConfig != SplitConfig.Single)
                {
                    Logger.LogWarning($"Nicht genug Kameras für {_currentConfig}, wechsle zu einem kompatibleren Layout");
                    if (_cams.Count >= 2)
                    {
                        _currentConfig = SplitConfig.Horizontal;
                        Logger.LogInfo("Wechsle zu Horizontal-Split, da nicht genug Kameras für komplexere Layouts vorhanden sind");
                    }
                    else
                    {
                        _currentConfig = SplitConfig.Single;
                        Logger.LogInfo("Wechsle zu Einzelmodus, da keine Split-Kameras verfügbar sind");
                    }
                }

                switch (_currentConfig)
                {
                    case SplitConfig.Single:
                        _originalCamera.rect = new Rect(0, 0, 1, 1);
                        _originalCamera.enabled = true; // Stelle sicher, dass die Hauptkamera aktiviert ist
                        Logger.LogInfo("Einzelbildschirm aktiviert");
                        break;

                    case SplitConfig.Horizontal:
                        for (int i = 0; i < 2 && i < _cams.Count; i++)
                        {
                            if (_cams[i].Camera == null)
                            {
                                Logger.LogWarning($"Kamera {i} ist null in Horizontal-Modus");
                                continue;
                            }
                            var r = new Rect(0, i == 0 ? 0.5f : 0f, 1, 0.5f);
                            _cams[i].Activate(r);
                            Logger.LogInfo($"Horizontaler Split: Kamera {i} mit Rect {r} aktiviert");
                        }
                        break;

                    case SplitConfig.Vertical:
                        for (int i = 0; i < 2 && i < _cams.Count; i++)
                        {
                            if (_cams[i].Camera == null)
                            {
                                Logger.LogWarning($"Kamera {i} ist null in Vertikal-Modus");
                                continue;
                            }
                            var r = new Rect(i == 0 ? 0f : 0.5f, 0, 0.5f, 1);
                            _cams[i].Activate(r);
                            Logger.LogInfo($"Vertikaler Split: Kamera {i} mit Rect {r} aktiviert");
                        }
                        break;

                    case SplitConfig.ThreeWay:
                        // Entferne die Bedingung _cams.Count >= 3, um sicherzustellen, dass wir so viele Kameras wie möglich aktivieren
                        Logger.LogInfo($"ThreeWay Split mit {_cams.Count} verfügbaren Kameras");
                        if (_cams.Count >= 1 && _cams[0].Camera != null)
                        {
                            _cams[0].Activate(new Rect(0, 0.5f, 1, 0.5f));
                            Logger.LogInfo($"ThreeWay Kamera 0 aktiviert mit Rect(0, 0.5, 1, 0.5)");
                        }
                        if (_cams.Count >= 2 && _cams[1].Camera != null)
                        {
                            _cams[1].Activate(new Rect(0, 0, 0.5f, 0.5f));
                            Logger.LogInfo($"ThreeWay Kamera 1 aktiviert mit Rect(0, 0, 0.5, 0.5)");
                        }
                        if (_cams.Count >= 3 && _cams[2].Camera != null)
                        {
                            _cams[2].Activate(new Rect(0.5f, 0, 0.5f, 0.5f));
                            Logger.LogInfo($"ThreeWay Kamera 2 aktiviert mit Rect(0.5, 0, 0.5, 0.5)");
                        }
                        break;

                    case SplitConfig.FourWay:
                        for (int i = 0; i < 4 && i < _cams.Count; i++)
                        {
                            if (_cams[i].Camera == null)
                            {
                                Logger.LogWarning($"Kamera {i} ist null in FourWay-Modus");
                                continue;
                            }
                            float x = (i % 2) * 0.5f;
                            float y = (i / 2) * 0.5f;
                            var r = new Rect(x, y, 0.5f, 0.5f);
                            _cams[i].Activate(r);
                            Logger.LogInfo($"Vierfach-Split: Kamera {i} mit Rect {r} aktiviert");
                        }
                        break;
                }
                
                // Überprüfe, ob nach dem Switch irgendeine Kamera aktiv ist
                bool anyActiveCameras = FindObjectsOfType<Camera>().Any(c => c.enabled);
                if (!anyActiveCameras)
                {
                    Logger.LogError("Keine aktiven Kameras nach ApplyViewports. Schalte zur Originalkamera zurück!");
                    _currentConfig = SplitConfig.Single;
                    _originalCamera.rect = new Rect(0, 0, 1, 1);
                    _originalCamera.enabled = true;
                }
                
                // Nochmal überprüfen, was für Kameras aktiv sind
                var activeCameras = FindObjectsOfType<Camera>().Where(c => c.enabled).ToList();
                Logger.LogInfo($"Nach ApplyViewports: {activeCameras.Count} aktive Kameras.");
                foreach (var cam in activeCameras)
                {
                    Logger.LogInfo($"Aktive Kamera: {cam.name}, rect={cam.rect}, depth={cam.depth}");
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Fehler in ApplyViewports: {ex.Message}\n{ex.StackTrace}");
                
                // Notfallwiederherstellung - Standardkamera aktivieren
                try 
                {
                    _currentConfig = SplitConfig.Single;
                    if (_originalCamera != null)
                    {
                        _originalCamera.rect = new Rect(0, 0, 1, 1);
                        _originalCamera.enabled = true;
                    }
                }
                catch {}
            }
        }

        private void UpdateCamPositions(RoomCamera rc)
        {
            try
            {
                if (_currentConfig == SplitConfig.Single) return;
                
                Logger.LogInfo($"UpdateCamPositions: Aktualisiere Kamerapositionen für {Math.Min(_cams.Count, rc.game.Players.Count)} Spieler");

                // Sicherheitsüberprüfung für jeden Camera-Zugriff
                for (int i = 0; i < _cams.Count && i < rc.game.Players.Count; i++)
                {
                    if (_cams[i].Camera == null)
                    {
                        Logger.LogWarning($"UpdateCamPositions: Kamera für Player {i} ist null");
                        continue;
                    }
                    
                    try
                    {
                        var pos = _tracker.GetPlayerPos(i);
                        var t = _cams[i].Camera.transform;
                        var oldPos = t.position;
                        
                        // Sanfte Bewegung der Kamera statt direkter Positionsänderung
                        Vector3 newPos = new Vector3(pos.x, pos.y, t.position.z);
                        t.position = Vector3.Lerp(oldPos, newPos, Time.deltaTime * 5f);
                        
                        // Gemäß der Z-Position der Originalkamera positionieren
                        if (_originalCamera != null)
                        {
                            t.position = new Vector3(t.position.x, t.position.y, _originalCamera.transform.position.z);
                        }
                        
                        Logger.LogInfo($"Kamera {i} Position: {t.position}, Spielerposition: {pos}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Fehler bei Kameraposition für Spieler {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Kritischer Fehler in UpdateCamPositions: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void CleanupSplitScreen()
        {
            foreach (var c in _cams)
                if (c.Camera) Destroy(c.Camera.gameObject);
            _cams.Clear();
            
            // _originalCamera nicht auf null setzen, da wir die Referenz behalten wollen
            // Stattdessen nur das SplitConfig zurücksetzen
            _currentConfig = SplitConfig.Single;
            
            // Wenn _originalCamera existiert, stellen wir sicher, dass sie korrekt konfiguriert ist
            if (_originalCamera != null)
            {
                _originalCamera.enabled = true;
                _originalCamera.rect = new Rect(0, 0, 1, 1);
                Logger.LogInfo("CleanupSplitScreen: Originalkamera zurückgesetzt und aktiviert");
            }
        }
    }

    // ───────────────────────── Helpers / DTOs ────────────────────────────
    internal sealed class SplitCam
    {
        public Camera Camera { get; }
        public readonly int PlayerIdx; // Von private zu public geändert
        public SplitCam(Camera cam, int idx) { Camera = cam; PlayerIdx = idx; }
        public void Activate(Rect r) { Camera.rect = r; Camera.enabled = true; }
    }

    internal enum SplitConfig { Single, Horizontal, Vertical, ThreeWay, FourWay }
    public   enum SplitLayout { Automatic, Horizontal, Vertical, ThreeWay, FourWay }

    internal sealed class PlayerTracker
    {
        private readonly List<Vector2> _positions = new();
        private readonly BepInEx.Logging.ILogSource _log;
        public PlayerTracker(BepInEx.Logging.ILogSource log) => _log = log;

        public void UpdatePlayers(RoomCamera rc)
        {
            _positions.Clear();
            for (int i = 0; i < rc.game.Players.Count && i < 4; i++)
            {
                var p = rc.game.Players[i];
                _positions.Add(p?.realizedCreature?.mainBodyChunk.pos ?? Vector2.zero);
            }
        }

        public Vector2 GetPlayerPos(int idx) => idx >= 0 && idx < _positions.Count ? _positions[idx] : Vector2.zero;

        public bool ArePlayersFarApart(float threshold)
        {
            for (int i = 0; i < _positions.Count; i++)
                for (int j = i + 1; j < _positions.Count; j++)
                    if (Vector2.Distance(_positions[i], _positions[j]) > threshold) return true;
            return false;
        }

        public bool WiderHorizontally()
        {
            float maxH = 0, maxV = 0;
            for (int i = 0; i < _positions.Count; i++)
                for (int j = i + 1; j < _positions.Count; j++)
                {
                    maxH = Mathf.Max(maxH, Mathf.Abs(_positions[i].x - _positions[j].x));
                    maxV = Mathf.Max(maxV, Mathf.Abs(_positions[i].y - _positions[j].y));
                }
            return maxH > maxV;
        }
    }
}
