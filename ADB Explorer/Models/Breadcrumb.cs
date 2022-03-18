using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using static ADB_Explorer.Models.Data;

namespace ADB_Explorer.Models
{
    internal static class AbstractBreadcrumbs
    {
        internal static void AddArrows(this ObservableList<Breadcrumb> items)
        {
            for (int i = 1; i < items.Count; i += 2)
            {
                items.Insert(i, new() { Type = Breadcrumb.CrumbType.Arrow });
            }
        }

    }

    public class Breadcrumb : INotifyPropertyChanged
    {
        public enum CrumbType
        {
            Folder,
            Excess,
            Arrow,
        }

        public CrumbType Type { get; set; }
        public string DisplayName { get; set; }
        public string Path { get; set; }
        public ObservableList<Breadcrumb> Children { get; set; }

        public Breadcrumb()
        { }

        public Breadcrumb(string path, string displayName)
        {
            Path = path;
            DisplayName = displayName == "" ? path : displayName;
            Type = CrumbType.Folder;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public override string ToString()
        {
            return Type is CrumbType.Folder ? DisplayName : Type.ToString();
        }
    }

    public class CrumbPath : INotifyPropertyChanged
    {
        public ObservableList<Breadcrumb> Items { get; private set; } = new();
        public string FullPath { get; private set; }
        public double LastWidth { get; set; }

        public CrumbPath(string path = "")
        {
            FullPath = path;
            Items = GeneratePath(path);
            Items.AddArrows();
        }

        public void UpdatePath(string path)
        {
            FullPath = path;
            Items = GeneratePath(path);
            Items.AddArrows();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private static ObservableList<Breadcrumb> GeneratePath(string path) => GeneratePath(path, CurrentPrettyNames);

        public static ObservableList<Breadcrumb> GeneratePath(string path, Dictionary<string, string> prettyNames)
        {
            ObservableList<Breadcrumb> items = new();

            var index = -1;
            while (index < path.Length)
            {
                var prev = index + 1;
                
                string dirName = "";
                if (prev == 0
                    && prettyNames.Where(kv => path.StartsWith(kv.Key)) is var names
                    && names.Any()
                    && names.First() is var pName)
                {
                    dirName = pName.Value;
                    index = pName.Key.Length;
                }
                else
                {
                    index = path.IndexOf('/', index + 1);
                }

                Index stop;
                if (index == -1)
                    stop = ^0;
                else if (index == 0)
                    stop = 1;
                else
                    stop = index;

                if (dirName == "")
                    dirName = path[prev..stop];

                if (dirName.Length > 0)
                    items.Add(new(path[..stop], dirName));

                if (index == -1)
                    break;
            }

            return items;
        }

        public void ShortenPath(int count = 1)
        {
            if (Items[0].Type is not Breadcrumb.CrumbType.Excess)
            {
                Items.Insert(0, new() { Type = Breadcrumb.CrumbType.Excess });
                Items[0].Children = new();
            }

            while (count > 0)
            {
                if (Items[1].Type is Breadcrumb.CrumbType.Arrow)
                    Items.RemoveAt(1);

                Items[0].Children.Add(Items.Pop(1));

                count--;
            }
        }

        public void LengthenPath(int count = 1)
        {
            if (Items[0].Type is not Breadcrumb.CrumbType.Excess)
                return;

            while (count > 0)
            {
                Items.Insert(1, Items[0].Children.Pop(^1));

                if (Items[0].Children.Count == 0)
                {
                    Items.RemoveAt(0);
                }

                count--;
            }
        }
    }
}
