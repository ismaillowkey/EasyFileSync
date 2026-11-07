using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyFileSync.Core.Models;
using Microsoft.Data.Sqlite;

namespace EasyFileSync.Core.Engine;

public sealed class AppPaths
{
    public string Root { get; }
    public string DbFile
    {
        get
        {
            string newDb = Path.Combine(Root, "EasyFileSync.db");
            string legacyDb = Path.Combine(Root, "badsync.db");
            if (!File.Exists(newDb) && File.Exists(legacyDb))
            {
                try
                {
                    File.Copy(legacyDb, newDb, true);
                }
                catch
                {
                }
            }
            return newDb;
        }
    }
    public string StateDir => Path.Combine(Root, "state");
    public string LogDir => Path.Combine(Root, "logs");

    public AppPaths(string? root = null)
    {
        if (root != null)
        {
            Root = root;
        }
        else
        {
            string defaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EasyFileSync");
            string legacyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BadSync");

            // Migrasi otomatis data lama dari BadSync ke EasyFileSync jika belum ada
            if (!Directory.Exists(defaultRoot) && Directory.Exists(legacyRoot))
            {
                try
                {
                    Directory.CreateDirectory(defaultRoot);
                    foreach (string file in Directory.GetFiles(legacyRoot, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(legacyRoot, file);
                        string dest = Path.Combine(defaultRoot, rel);
                        string? dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        File.Copy(file, dest, true);
                    }
                }
                catch
                {
                }
            }

            Root = defaultRoot;
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(LogDir);
    }
}

public sealed class JobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly string _connectionString;
    private readonly object _sync = new();
    private List<JobConfig> _jobs = new();

    public JobStore(AppPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DbFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeDatabase();
        LoadFromDatabase();
    }

    public IReadOnlyList<JobConfig> Jobs
    {
        get
        {
            lock (_sync)
            {
                return _jobs.ToList();
            }
        }
    }

    /// <summary>
    /// Membuat skema tabel di SQLite jika belum tersedia.
    /// </summary>
    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS jobs (
                name TEXT PRIMARY KEY,
                group_name TEXT,
                config_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS states (
                job_name TEXT PRIMARY KEY,
                state_json TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Memuat daftar konfigurasi job dari database SQLite.
    /// </summary>
    private void LoadFromDatabase()
    {
        lock (_sync)
        {
            _jobs.Clear();

            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT config_json FROM jobs ORDER BY rowid ASC;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string json = reader.GetString(0);
                    var job = JsonSerializer.Deserialize<JobConfig>(json, JsonOptions);
                    if (job != null)
                    {
                        _jobs.Add(job);
                    }
                }
            }
            catch
            {
                _jobs = new List<JobConfig>();
            }

            // Jika database SQLite masih kosong tetapi ada file jobs.json lama, migrasikan datanya
            if (_jobs.Count == 0)
            {
                string oldJsonFile = Path.Combine(_paths.Root, "jobs.json");
                if (File.Exists(oldJsonFile))
                {
                    try
                    {
                        string oldJson = File.ReadAllText(oldJsonFile);
                        var oldJobs = JsonSerializer.Deserialize<List<JobConfig>>(oldJson, JsonOptions);
                        if (oldJobs != null)
                        {
                            foreach (var job in oldJobs)
                            {
                                AddToDb(job);
                                _jobs.Add(job);
                            }
                        }
                    }
                    catch
                    {
                        // Abaikan jika file lama korup
                    }
                }
            }
        }
    }

    private void AddToDb(JobConfig job)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO jobs (name, group_name, config_json)
            VALUES (@name, @group_name, @config_json)
            ON CONFLICT(name) DO UPDATE SET
                group_name = excluded.group_name,
                config_json = excluded.config_json;";

        cmd.Parameters.AddWithValue("@name", job.Name);
        cmd.Parameters.AddWithValue("@group_name", job.Group ?? "");
        cmd.Parameters.AddWithValue("@config_json", JsonSerializer.Serialize(job, JsonOptions));

        cmd.ExecuteNonQuery();
    }

    private void RemoveFromDb(string name)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE name = @name;";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.ExecuteNonQuery();
    }

    public void Save()
    {
        lock (_sync)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            using var cmdDelete = conn.CreateCommand();
            cmdDelete.Transaction = tx;
            cmdDelete.CommandText = "DELETE FROM jobs;";
            cmdDelete.ExecuteNonQuery();

            foreach (var job in _jobs)
            {
                using var cmdInsert = conn.CreateCommand();
                cmdInsert.Transaction = tx;
                cmdInsert.CommandText = @"
                    INSERT INTO jobs (name, group_name, config_json)
                    VALUES (@name, @group_name, @config_json);";

                cmdInsert.Parameters.AddWithValue("@name", job.Name);
                cmdInsert.Parameters.AddWithValue("@group_name", job.Group ?? "");
                cmdInsert.Parameters.AddWithValue("@config_json", JsonSerializer.Serialize(job, JsonOptions));
                cmdInsert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public JobConfig? Find(string name)
    {
        lock (_sync)
        {
            foreach (var job in _jobs)
            {
                if (string.Equals(job.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return job;
                }
            }
            return null;
        }
    }

    public bool NameExists(string name)
    {
        var existing = Find(name);
        if (existing != null)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    public string UniqueName(string baseName)
    {
        string name = baseName;
        int i = 2;
        while (NameExists(name))
        {
            name = baseName + " (" + i + ")";
            i = i + 1;
        }
        return name;
    }

    public void Add(JobConfig job)
    {
        lock (_sync)
        {
            _jobs.Add(job);
            AddToDb(job);
        }
    }

    public void Update(JobConfig job)
    {
        lock (_sync)
        {
            int idx = -1;
            for (int i = 0; i < _jobs.Count; i++)
            {
                if (string.Equals(_jobs[i].Name, job.Name, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {
                _jobs[idx] = job;
            }
            else
            {
                _jobs.Add(job);
            }

            AddToDb(job);
        }
    }

    public bool Remove(string name)
    {
        bool removed = false;
        lock (_sync)
        {
            int countBefore = _jobs.Count;
            _jobs.RemoveAll(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
            if (_jobs.Count < countBefore)
            {
                removed = true;
                RemoveFromDb(name);
            }
        }
        return removed;
    }

    public bool Rename(string oldName, string newName)
    {
        lock (_sync)
        {
            var job = Find(oldName);
            if (job == null)
            {
                return false;
            }

            RemoveFromDb(oldName);
            job.Name = newName;
            AddToDb(job);
            return true;
        }
    }

    public void MoveToGroup(string jobName, string group)
    {
        var job = Find(jobName);
        if (job == null)
        {
            return;
        }

        if (group == null)
        {
            job.Group = "";
        }
        else
        {
            job.Group = group;
        }

        AddToDb(job);
    }

    /// <summary>
    /// Mengambil semua daftar nama grup unik dari database settings dan konfigurasi job.
    /// </summary>
    public List<string> GetGroups()
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ambil grup yang tersimpan di tabel settings
        string? json = GetSetting("groups");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    foreach (var g in list)
                    {
                        if (!string.IsNullOrWhiteSpace(g))
                        {
                            groups.Add(g.Trim());
                        }
                    }
                }
            }
            catch
            {
                // Abaikan jika json korup
            }
        }

        // Ambil juga grup dari setiap job yang ada
        lock (_sync)
        {
            foreach (var job in _jobs)
            {
                if (!string.IsNullOrWhiteSpace(job.Group))
                {
                    groups.Add(job.Group.Trim());
                }
            }
        }

        return groups.OrderBy(g => g).ToList();
    }

    /// <summary>
    /// Menambahkan grup baru ke daftar grup yang tersimpan.
    /// </summary>
    public void AddGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        string trimmed = groupName.Trim();
        var groups = GetGroups();
        bool exists = false;
        foreach (var g in groups)
        {
            if (string.Equals(g, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            groups.Add(trimmed);
            SaveGroups(groups);
        }
    }

    /// <summary>
    /// Menyimpan daftar grup ke tabel settings di SQLite.
    /// </summary>
    public void SaveGroups(List<string> groups)
    {
        string json = JsonSerializer.Serialize(groups, JsonOptions);
        SetSetting("groups", json);
    }

    /// <summary>
    /// Mengubah nama grup dan memperbarui seluruh job yang berada di dalam grup tersebut.
    /// </summary>
    public void RenameGroup(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        string trimmedOld = oldName.Trim();
        string trimmedNew = newName.Trim();

        var groups = GetGroups();
        groups.RemoveAll(g => string.Equals(g, trimmedOld, StringComparison.OrdinalIgnoreCase));

        bool newExists = false;
        foreach (var g in groups)
        {
            if (string.Equals(g, trimmedNew, StringComparison.OrdinalIgnoreCase))
            {
                newExists = true;
                break;
            }
        }

        if (!newExists)
        {
            groups.Add(trimmedNew);
        }
        SaveGroups(groups);

        // Perbarui semua job yang menggunakan nama grup lama
        lock (_sync)
        {
            foreach (var job in _jobs)
            {
                if (string.Equals(job.Group, trimmedOld, StringComparison.OrdinalIgnoreCase))
                {
                    job.Group = trimmedNew;
                    AddToDb(job);
                }
            }
        }
    }

    /// <summary>
    /// Menghapus grup dan melepaskan (ungroup) seluruh job di dalamnya kembali ke root.
    /// </summary>
    public void DeleteGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        string trimmed = groupName.Trim();
        var groups = GetGroups();
        groups.RemoveAll(g => string.Equals(g, trimmed, StringComparison.OrdinalIgnoreCase));
        SaveGroups(groups);

        // Reset grup pada job yang menggunakannya
        lock (_sync)
        {
            foreach (var job in _jobs)
            {
                if (string.Equals(job.Group, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    job.Group = "";
                    AddToDb(job);
                }
            }
        }
    }

    /// <summary>
    /// Membaca setting dari tabel settings di database SQLite.
    /// </summary>
    public string? GetSetting(string key)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @key LIMIT 1;";
        cmd.Parameters.AddWithValue("@key", key);

        var result = cmd.ExecuteScalar();
        if (result != null)
        {
            return result.ToString();
        }
        else
        {
            return null;
        }
    }

    /// <summary>
    /// Menyimpan setting ke tabel settings di database SQLite.
    /// </summary>
    public void SetSetting(string key, string value)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO settings (key, value)
            VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;";

        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value ?? "");
        cmd.ExecuteNonQuery();
    }
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _connectionString;

    public StateStore(AppPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DbFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public Dictionary<string, StateEntry> Load(string jobName)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT state_json FROM states WHERE job_name = @job_name LIMIT 1;";
            cmd.Parameters.AddWithValue("@job_name", jobName);

            var result = cmd.ExecuteScalar();
            if (result != null)
            {
                string json = result.ToString()!;
                var data = JsonSerializer.Deserialize<Dictionary<string, StateEntry>>(json, JsonOptions);
                if (data != null)
                {
                    return new Dictionary<string, StateEntry>(data, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
        }

        return new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public void Save(string jobName, Dictionary<string, StateEntry> state)
    {
        try
        {
            string json = JsonSerializer.Serialize(state, JsonOptions);

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO states (job_name, state_json)
                VALUES (@job_name, @state_json)
                ON CONFLICT(job_name) DO UPDATE SET state_json = excluded.state_json;";

            cmd.Parameters.AddWithValue("@job_name", jobName);
            cmd.Parameters.AddWithValue("@state_json", json);
            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }

    public void Delete(string jobName)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM states WHERE job_name = @job_name;";
            cmd.Parameters.AddWithValue("@job_name", jobName);
            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }
}
