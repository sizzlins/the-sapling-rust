using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SaplingRustBridge
{
    /// <summary>
    /// Read-only RCON for the retail game.
    ///
    /// Same shape as the Rust port's <c>rcon.rs</c>: a line protocol on loopback, a
    /// background accept thread, and a queue drained on the Unity main thread so no
    /// game object is ever touched from a socket thread.
    ///
    /// Port 27016 because 27015 belongs to the port's own RCON.
    /// </summary>
    public static class RconServer
    {
        public const int Port = 27016;
        const string BindAddress = "127.0.0.1";
        // Species extermination walks every area and can take seconds; a frozen
        // main thread (save/load) must not be reported as a dead server.
        const int RequestTimeoutMs = 120000;

        /// <summary>
        /// Bound from BepInEx config <c>[Rcon] AllowMutation</c>. When false, every
        /// command that changes the world refuses. Read the backup before enabling.
        /// </summary>
        public static bool AllowMutation;

        sealed class Request
        {
            public string Command;
            public string Response;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        static readonly object QueueLock = new object();
        static readonly Queue<Request> Pending = new Queue<Request>();

        static TcpListener _listener;
        static Thread _acceptThread;
        static volatile bool _running;

        static WorldController _world;
        static float _nextWorldLookup;

        public static bool Running { get { return _running; } }

        public static void Start()
        {
            if (_running) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _running = true;

                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "sapling-rcon-accept";
                _acceptThread.Start();

                Plugin.LogInfo("[RCON] Listening on tcp://" + BindAddress + ":" + Port + " (read-only)");
            }
            catch (Exception ex)
            {
                _running = false;
                Plugin.LogWarn("[RCON] Failed to bind " + BindAddress + ":" + Port + ": " + ex.Message);
            }
        }

        public static void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }

        static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    if (!_running) return;
                    continue;
                }

                Thread t = new Thread(ClientLoop);
                t.IsBackground = true;
                t.Name = "sapling-rcon-client";
                t.Start(client);
            }
        }

        static void ClientLoop(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine("Sapling RCON v0.1 (read-only). Type 'help'.");

                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        if (line == "quit" || line == "exit")
                        {
                            writer.WriteLine("Bye.");
                            return;
                        }

                        Request req = new Request();
                        req.Command = line;
                        lock (QueueLock) { Pending.Enqueue(req); }

                        if (!req.Done.WaitOne(RequestTimeoutMs))
                        {
                            writer.WriteLine("{\"error\":\"timeout: main thread busy\"}");
                            continue;
                        }

                        writer.WriteLine(req.Response);
                    }
                }
            }
            catch
            {
                // a dropped client is not an error
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        /// <summary>Drain queued commands. Call once per frame from Plugin.Update().</summary>
        public static void Tick()
        {
            while (true)
            {
                Request req;
                lock (QueueLock)
                {
                    if (Pending.Count == 0) return;
                    req = Pending.Dequeue();
                }

                try
                {
                    req.Response = Dispatch(req.Command);
                }
                catch (Exception ex)
                {
                    req.Response = JsonError("dispatch failed: " + ex.Message);
                }
                finally
                {
                    req.Done.Set();
                }
            }
        }

        static WorldController World()
        {
            if (_world == null && Time.realtimeSinceStartup >= _nextWorldLookup)
            {
                _nextWorldLookup = Time.realtimeSinceStartup + 2f;
                _world = UnityEngine.Object.FindObjectOfType<WorldController>();
            }
            return _world;
        }

        static string Dispatch(string command)
        {
            string[] parts = command.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

            switch (cmd)
            {
                case "help": return Help();
                case "status": return Status();
                case "species": return Species(parts);
                case "find": return Find(parts);
                case "count": return Counts();
                case "kill_animals": return KillAnimals(parts);
                case "kill": return KillCommand(parts);
                case "quit":
                case "exit": return "{\"status\":\"ok\"}";
                case "eval": return JsonError("eval is disabled in the read-only build");
                default: return JsonError("unknown command: " + cmd);
            }
        }

        static string Help()
        {
            return "Commands:\n" +
                   "- status                : day, years, season, temperature, speed, counts\n" +
                   "- count                 : per-kingdom species and living organism totals\n" +
                   "- species [offset] [n]  : page of species rows (default 0 50, max 500)\n" +
                   "- species <id>          : one species with lineage and population\n" +
                   "- find <text>           : case-insensitive species-name search (max 50)\n" +
                   "- kill <kingdom> confirm: exterminate every species of alga|plant|animal|fungus\n" +
                   "- kill_animals confirm  : alias for 'kill animal confirm'\n" +
                   "- quit                  : close the connection\n" +
                   "Mutation is refused unless [Rcon] AllowMutation = true in the BepInEx config.";
        }

        static string Status()
        {
            WorldController wc = World();
            if (wc == null) return JsonError("no WorldController (not in a world yet)");
            TimeLine tl = wc.timeLine;
            if (tl == null) return JsonError("WorldController has no TimeLine");

            int day = tl.dayIndex;
            int living = 0;
            int algae = 0, plants = 0, animals = 0, fungi = 0;
            int speciesCount = tl.allSpecies != null ? tl.allSpecies.Count : 0;

            for (int i = 0; i < speciesCount; i++)
            {
                SpeciesDefinition s = tl.allSpecies[i];
                int pop = Population(tl, i);
                living += pop;
                if (s == null) continue;
                switch (s.speciesType)
                {
                    case OrganismType.Alga: algae++; break;
                    case OrganismType.Plant: plants++; break;
                    case OrganismType.Animal: animals++; break;
                    case OrganismType.Fungus: fungi++; break;
                }
            }

            StringBuilder b = new StringBuilder();
            b.Append("{\"day\":").Append(day);
            b.Append(",\"years\":").Append(day / 365);
            b.Append(",\"historyDays\":").Append(tl.line != null ? tl.line.Count : 0);
            b.Append(",\"temperature\":").Append(F(tl.globalTemperature));
            b.Append(",\"season\":").Append(tl.currentSeasonIndex);
            b.Append(",\"usesSeasons\":").Append(tl.useSeasons ? "true" : "false");
            b.Append(",\"speed\":\"").Append(wc.timeSpeedModus.ToString()).Append("\"");
            b.Append(",\"paused\":").Append(wc.timeSpeedModus == TimeSpeedModus.Paused ? "true" : "false");
            b.Append(",\"jumpAheadDays\":").Append(wc.numberOfDaysToJumpAhead);
            b.Append(",\"species\":").Append(speciesCount);
            b.Append(",\"living\":").Append(living);
            b.Append(",\"algae\":").Append(algae);
            b.Append(",\"plants\":").Append(plants);
            b.Append(",\"animals\":").Append(animals);
            b.Append(",\"fungi\":").Append(fungi);
            b.Append("}");
            return b.ToString();
        }

        /// <summary>
        /// Exterminate every species whose type is Animal, using the game's own path
        /// (`TimeLine.ExterminateSpecies`) so death causes, eggs and trackers stay
        /// consistent. Plants, fungi, algae, grass and carrion are untouched.
        /// </summary>
        static string KillAnimals(string[] parts)
        {
            return KillKingdom(parts, OrganismType.Animal);
        }

        static string KillCommand(string[] parts)
        {
            if (parts.Length < 2) return JsonError("Usage: kill <alga|plant|animal|fungus> confirm");
            switch (parts[1].ToLowerInvariant())
            {
                case "alga":
                case "algae": return KillKingdom(parts, OrganismType.Alga);
                case "plant":
                case "plants": return KillKingdom(parts, OrganismType.Plant);
                case "animal":
                case "animals": return KillKingdom(parts, OrganismType.Animal);
                case "fungus":
                case "fungi": return KillKingdom(parts, OrganismType.Fungus);
                default: return JsonError("unknown kingdom: " + parts[1]);
            }
        }

        static string KillKingdom(string[] parts, OrganismType type)
        {
            if (!AllowMutation)
                return JsonError("mutation disabled: set [Rcon] AllowMutation = true in BepInEx/config/com.sizzlins.saplingrust.cfg and restart");

            if (parts.Length < 2 || !string.Equals(parts[parts.Length - 1], "confirm", StringComparison.OrdinalIgnoreCase))
                return JsonError("Usage: kill <alga|plant|animal|fungus> confirm");

            WorldController wc = World();
            if (wc == null || wc.timeLine == null) return JsonError("no world");
            TimeLine tl = wc.timeLine;
            if (tl.allSpecies == null) return JsonError("no species table");

            int killedSpecies = 0;
            int removed = 0;
            int failed = 0;
            List<int> lifeForms = new List<int>();
            int before = LivingOfType(tl, type);

            for (int i = 0; i < tl.allSpecies.Count; i++)
            {
                SpeciesDefinition s = tl.allSpecies[i];
                if (s == null || s.speciesType != type) continue;
                killedSpecies++;
                try
                {
                    List<int> gone = tl.ExterminateSpecies(i);
                    if (gone != null)
                    {
                        removed += gone.Count;
                        lifeForms.AddRange(gone);
                    }
                    // The per-day counter is normally refreshed by LiveADay; zero it now so
                    // status/count reads are correct before the next tick.
                    if (tl.today != null && tl.today.numberOfLivingOrganismsPerSpecies != null &&
                        i < tl.today.numberOfLivingOrganismsPerSpecies.Count)
                    {
                        tl.today.numberOfLivingOrganismsPerSpecies[i] = 0;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            // The species-detail path cleans the views and refreshes the UI; mirror it.
            try
            {
                if (wc.view != null && tl.today != null && tl.today.areas != null)
                {
                    for (int a = 0; a < tl.today.areas.Length; a++)
                    {
                        WorldArea area = tl.today.areas[a];
                        if (type == OrganismType.Alga)
                        {
                            wc.view.CleanUpAlgaViewsInArea(area.indicesOfCellsWhereAMacroAlgaDied, a, false);
                            wc.view.CleanUpAlgaViewsInArea(area.indicesOfCellsWherePlanktonDied, a, true);
                        }
                        else if (type == OrganismType.Plant)
                        {
                            wc.view.CleanUpPlantViewsInArea(area.indicesOfCellsWhereAPlantDied, a);
                        }
                        else if (type == OrganismType.Fungus)
                        {
                            wc.view.CleanUpFungusViewsInArea(area.indicesOfCellsWhereAFungusDied, a);
                        }
                        else
                        {
                            wc.view.CleanUpAnimalViewsWithLifeFormIndicesInArea(lifeForms, a);
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (wc.notificator != null && tl.deathTracker != null)
                {
                    wc.notificator.ProcessNewState(tl.allSpecies.ToArray(), tl.today.numberOfLivingOrganismsPerSpecies, tl.deathTracker);
                }
            }
            catch { }

            try { wc.ShowToday(); } catch { }

            StringBuilder b = new StringBuilder();
            b.Append("{\"kingdom\":\"").Append(type.ToString()).Append("\"");
            b.Append(",\"species\":").Append(killedSpecies);
            b.Append(",\"removed\":").Append(removed);
            b.Append(",\"failedSpecies\":").Append(failed);
            b.Append(",\"livingBefore\":").Append(before);
            b.Append(",\"livingAfter\":").Append(LivingOfType(tl, type));
            b.Append("}");
            return b.ToString();
        }

        static int LivingOfType(TimeLine tl, OrganismType type)
        {
            if (tl.today == null || tl.today.numberOfLivingOrganismsPerSpecies == null || tl.allSpecies == null) return 0;
            int total = 0;
            int n = tl.allSpecies.Count;
            for (int i = 0; i < n; i++)
            {
                SpeciesDefinition s = tl.allSpecies[i];
                if (s != null && s.speciesType == type) total += Population(tl, i);
            }
            return total;
        }

        static string Counts()
        {
            WorldController wc = World();
            if (wc == null || wc.timeLine == null) return JsonError("no world");
            TimeLine tl = wc.timeLine;

            int algae = 0, plants = 0, animals = 0, fungi = 0;
            int livingAlgae = 0, livingPlants = 0, livingAnimals = 0, livingFungi = 0;
            int n = tl.allSpecies != null ? tl.allSpecies.Count : 0;
            for (int i = 0; i < n; i++)
            {
                SpeciesDefinition s = tl.allSpecies[i];
                if (s == null) continue;
                int pop = Population(tl, i);
                switch (s.speciesType)
                {
                    case OrganismType.Alga: algae++; livingAlgae += pop; break;
                    case OrganismType.Plant: plants++; livingPlants += pop; break;
                    case OrganismType.Animal: animals++; livingAnimals += pop; break;
                    case OrganismType.Fungus: fungi++; livingFungi += pop; break;
                }
            }

            return "{\"species\":{\"alga\":" + algae + ",\"plant\":" + plants + ",\"animal\":" + animals + ",\"fungus\":" + fungi + "}" +
                   ",\"living\":{\"alga\":" + livingAlgae + ",\"plant\":" + livingPlants + ",\"animal\":" + livingAnimals + ",\"fungus\":" + livingFungi + "}}";
        }

        static string Species(string[] parts)
        {
            WorldController wc = World();
            if (wc == null || wc.timeLine == null) return JsonError("no world");
            TimeLine tl = wc.timeLine;
            int n = tl.allSpecies != null ? tl.allSpecies.Count : 0;

            if (parts.Length == 2 && !int.TryParse(parts[1], out int _))
                return JsonError("species id must be an integer");

            // species <id>
            if (parts.Length == 2)
            {
                int id;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    return JsonError("bad species id");
                if (id < 0 || id >= n) return JsonError("species id out of range 0.." + (n - 1));

                SpeciesDefinition s = tl.allSpecies[id];
                if (s == null) return JsonError("species " + id + " is null");

                StringBuilder b = new StringBuilder();
                b.Append("{\"id\":").Append(id);
                b.Append(",\"name\":\"").Append(Esc(s.name)).Append("\"");
                b.Append(",\"type\":\"").Append(s.speciesType.ToString()).Append("\"");
                b.Append(",\"status\":\"").Append(s.status.ToString()).Append("\"");
                b.Append(",\"living\":").Append(Population(tl, id));
                b.Append(",\"peak\":").Append(s.peakNumberOfLivingOrganisms);
                b.Append(",\"birthYear\":").Append(s.birthYear);
                b.Append(",\"extinctSinceYear\":").Append(s.extinctSinceYear);
                b.Append(",\"yearsNotExtinct\":").Append(s.numberOfYearsNotExtinct);
                b.Append(",\"manuallyAdded\":").Append(s.manuallyAdded ? "true" : "false");
                b.Append(",\"parent\":").Append(Parent(tl, id));
                b.Append(",\"birthPlaces\":").Append(s.birthPlaces != null ? s.birthPlaces.Count : 0);
                b.Append("}");
                return b.ToString();
            }

            // species [offset] [limit]
            int offset = 0;
            int limit = 50;
            if (parts.Length >= 2) int.TryParse(parts[1], out offset);
            if (parts.Length >= 3) int.TryParse(parts[2], out limit);
            if (offset < 0) offset = 0;
            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"total\":").Append(n).Append(",\"offset\":").Append(offset).Append(",\"rows\":[");
            int written = 0;
            for (int i = offset; i < n && written < limit; i++)
            {
                SpeciesDefinition s = tl.allSpecies[i];
                if (s == null) continue;
                if (written > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(i);
                sb.Append(",\"name\":\"").Append(Esc(s.name)).Append("\"");
                sb.Append(",\"type\":\"").Append(s.speciesType.ToString()).Append("\"");
                sb.Append(",\"living\":").Append(Population(tl, i));
                sb.Append(",\"peak\":").Append(s.peakNumberOfLivingOrganisms);
                sb.Append(",\"status\":\"").Append(s.status.ToString()).Append("\"");
                sb.Append("}");
                written++;
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string Find(string[] parts)
        {
            if (parts.Length < 2) return JsonError("Usage: find <text>");
            string needle = string.Join(" ", parts, 1, parts.Length - 1);

            WorldController wc = World();
            if (wc == null || wc.timeLine == null) return JsonError("no world");
            TimeLine tl = wc.timeLine;

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"query\":\"").Append(Esc(needle)).Append("\",\"rows\":[");
            int written = 0;
            int n = tl.allSpecies != null ? tl.allSpecies.Count : 0;
            for (int i = 0; i < n && written < 50; i++)
            {
                SpeciesDefinition s = tl.allSpecies[i];
                if (s == null || s.name == null) continue;
                if (s.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (written > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(i);
                sb.Append(",\"name\":\"").Append(Esc(s.name)).Append("\"");
                sb.Append(",\"type\":\"").Append(s.speciesType.ToString()).Append("\"");
                sb.Append(",\"living\":").Append(Population(tl, i));
                sb.Append("}");
                written++;
            }
            sb.Append("],\"truncated\":").Append(written >= 50 ? "true" : "false").Append("}");
            return sb.ToString();
        }

        static int Population(TimeLine tl, int speciesIndex)
        {
            if (tl.today == null || tl.today.numberOfLivingOrganismsPerSpecies == null) return 0;
            List<int> counts = tl.today.numberOfLivingOrganismsPerSpecies;
            if (speciesIndex < 0 || speciesIndex >= counts.Count) return 0;
            return counts[speciesIndex];
        }

        static int Parent(TimeLine tl, int speciesIndex)
        {
            if (tl.parentSpeciesIndexPerSpeciesIndex == null) return -1;
            int parent;
            return tl.parentSpeciesIndexPerSpeciesIndex.TryGetValue(speciesIndex, out parent) ? parent : -1;
        }

        static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string JsonError(string message)
        {
            return "{\"error\":\"" + Esc(message) + "\"}";
        }

        static string Esc(string text)
        {
            if (text == null) return "";
            StringBuilder b = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '\\': b.Append("\\\\"); break;
                    case '"': b.Append("\\\""); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default: b.Append(c); break;
                }
            }
            return b.ToString();
        }
    }
}
