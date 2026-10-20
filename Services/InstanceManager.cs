using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AetherisLauncher.Models;

namespace AetherisLauncher.Services
{
    public class InstanceManager
    {
        private readonly string _rootDirectory;

        public InstanceManager(string? customRoot = null)
        {
            _rootDirectory = customRoot ?? AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(GetInstancesRootDirectory());
        }

        public string GetInstancesRootDirectory()
        {
            return Path.Combine(_rootDirectory, "instances");
        }

        public List<InstanceInfo> GetAllInstances()
        {
            var list = new List<InstanceInfo>();
            var root = GetInstancesRootDirectory();

            if (!Directory.Exists(root))
                return list;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var infoPath = Path.Combine(dir, "instance.json");
                if (File.Exists(infoPath))
                {
                    try
                    {
                        var json = File.ReadAllText(infoPath);
                        var instance = JsonSerializer.Deserialize<InstanceInfo>(json);
                        if (instance != null)
                        {
                            // Ensure Id matches folder name
                            instance.Id = Path.GetFileName(dir);
                            list.Add(instance);
                        }
                    }
                    catch
                    {
                        // Ignore corrupt configs or continue
                    }
                }
            }

            return list;
        }

        public void SaveInstance(InstanceInfo instance)
        {
            var dir = instance.GetDirectory(_rootDirectory);
            var infoPath = Path.Combine(dir, "instance.json");
            var json = JsonSerializer.Serialize(instance, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(infoPath, json);
        }

        public void DeleteInstance(InstanceInfo instance)
        {
            var dir = instance.GetDirectory(_rootDirectory);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        public string GetInstanceGameDir(InstanceInfo instance)
        {
            // Fully isolated instance folder
            return instance.GetDirectory(_rootDirectory);
        }
    }
}
