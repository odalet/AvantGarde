// -----------------------------------------------------------------------------
// PROJECT   : Avant Garde
// COPYRIGHT : Andy Thomas
// LICENSE   : GPLv3
// HOMEPAGE  : https://kuiper.zone/avantgarde-avalonia/
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AvantGarde.Projects
{
    /// <summary>
    /// Class which extends <see cref="PathItem"/> to provide a file heirarchy within a project.
    /// </summary>
    public class NodeItem : PathItem
    {
        private static readonly EnumerationOptions EnumerateOptions = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            MatchCasing = MatchCasing.PlatformDefault,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple
        };

        private static readonly IReadOnlyList<NodeItem> EmptyContents = new List<NodeItem>();

        private readonly List<NodeItem>? _contents;
        private DateTime _lastUtc;
        private int _hashCode;
        private string? _lastPattern;

        /// <summary>
        /// Constructor with path string and shared project.
        /// See base constructor.
        /// </summary>
        /// <exception cref="ArgumentException">Path is empty"</exception>
        public NodeItem(string path, PathKind kind, DotnetProject? project = null)
            : base(path, kind)
        {
            Project = project;
            Properties = project?.Solution?.Properties ?? new NodeProperties();

            _lastUtc = base.LastUtc;
            _hashCode = base.GetHashCode();

            if (IsDirectory)
            {
                _contents = new();
                Contents = _contents;
            }
            else
            {
                TotalFiles = Exists ? 1 : 0;
            }
        }

        /// <summary>
        /// Constructor. This instance will share the info object.
        /// </summary>
        public NodeItem(FileSystemInfo info, DotnetProject? project = null)
            : base(info)
        {
            Project = project;
            Properties = project?.Solution?.Properties ?? new NodeProperties();

            _lastUtc = base.LastUtc;
            _hashCode = base.GetHashCode();

            if (IsDirectory)
            {
                _contents = new();
                Contents = _contents;
            }
            else
            {
                TotalFiles = Exists ? 1 : 0;
            }
        }

        private NodeItem(FileSystemInfo info, NodeItem owner)
            : base(info)
        {
            // Internal constructor only
            Project = owner.Project;
            Properties = owner.Properties;
            Depth = owner.Depth + 1;
            Debug.Assert(Depth > -1);

            _lastUtc = base.LastUtc;
            _hashCode = base.GetHashCode();

            if (IsDirectory)
            {
                _contents = new();
                Contents = _contents;
            }
            else
            {
                TotalFiles = Exists ? 1 : 0;
            }
        }

        /// <summary>
        /// Gets the project to which this node belongs.
        /// </summary>
        public readonly DotnetProject? Project;

        /// <summary>
        /// Gets the <see cref="NodeProperties"/> instance. The instance may be shared with a common parent.
        /// Changes do not take effect until the owner instance is refreshed.
        /// </summary>
        public readonly NodeProperties Properties;

        /// <summary>
        /// Gets the file depth. Top nodes have depth 0.
        /// </summary>
        public readonly int Depth;

        /// <summary>
        /// Get the directory items. Items are sorted, with sub-directories listed before files. It is updated by
        /// <see cref="NodeItem.Refresh"/> and initially empty. The value is always empty for <see cref="PathKind.File"/>.
        /// </summary>
        public readonly IReadOnlyList<NodeItem> Contents = EmptyContents;

        /// <summary>
        /// Gets the total number of <see cref="PathKind.File"/> items in <see cref="Contents"/> and its
        /// subdirectories. It is updated by <see cref="NodeItem.Refresh"/>. For a directory, the initial value is 0
        /// until refresh. For a file, the value is always 1 where <see cref="Exists"/> is true, and 0 otherwise.
        /// </summary>
        public int TotalFiles { get; private set; }

        /// <summary>
        /// Overrides.
        /// </summary>
        public override DateTime LastUtc
        {
            get { return _lastUtc; }
        }

        /// <summary>
        /// Finds the first node with a matching name. If files is true, only files are found. If false, only directories.
        /// </summary>
        public NodeItem? FindFirst(string name, bool files, StringComparison comparison)
        {
            return FindInternal(CleanPath(name), files, comparison);
        }

        /// <summary>
        /// Overload with platform default case sensitivity.
        /// </summary>
        public NodeItem? FindFirst(string name, bool files)
        {
            return FindInternal(CleanPath(name), files, PathItem.PlatformComparison);
        }

        /// <summary>
        /// Finds a node with matching full name.
        /// </summary>
        public NodeItem? FindExact(string fullName, StringComparison comparison)
        {
            return FindInternal(CleanPath(fullName), null, comparison);
        }

        /// <summary>
        /// Overload with platform default case sensitivity.
        /// </summary>
        public NodeItem? FindExact(string fullName)
        {
            return FindInternal(CleanPath(fullName), null, PathItem.PlatformComparison);
        }

        /// <summary>
        /// Overrides <see cref="PathItem.Refresh"/>. For a file, this is equivalent to
        /// <see cref="PathItem.Refresh"/>. For a directory, it (re-)populates <see cref="Contents"/>.
        /// </summary>
        public override bool Refresh()
        {
            var changed = base.Refresh();
            _lastUtc = base.LastUtc;

            if (!IsDirectory || !Exists)
            {
                // Same behaviour as base for a file or not exist
                _hashCode = base.GetHashCode();

                _contents?.Clear();
                TotalFiles = Exists ? 1 : 0;
                return changed;
            }

            if (_contents != null && Properties != null)
            {
                // Existing directory
                // Sorted, with directories listed before files.
                var dirs = new SortedList<string, NodeItem>(_contents.Count);
                var files = new SortedList<string, NodeItem>(_contents.Count);

                // Keep existing if we can, but need
                // to do full rebuild if pattern changes
                if (_lastPattern == Properties.FilePatterns)
                {
                    // Copy existing and matching
                    foreach (var item in _contents)
                    {
                        if (item.IsDirectory)
                        {
                            if (Depth < Properties.SearchDepth && !Properties.IsExcluded(item.Name))
                            {
                                item.Refresh();

                                if (item.Exists && (Properties.ShowEmptyDirectories || item.TotalFiles > 0))
                                {
                                    dirs.Add(item.Name, item);
                                }
                            }
                        }
                        else
                        {
                            item.Refresh();

                            if (item.Exists)
                            {
                                files.Add(item.Name, item);
                            }
                        }
                    }
                }

                try
                {
                    var dirInfo = GetDirectoryInfo();

                    if (Depth < Properties.SearchDepth)
                    {
                        foreach (var item in dirInfo.EnumerateDirectories("*", EnumerateOptions))
                        {
                            if (!dirs.ContainsKey(item.Name) && !Properties.IsExcluded(item.Name))
                            {
                                var node = new NodeItem(item, this);
                                node.Refresh();

                                if (Properties.ShowEmptyDirectories || node.TotalFiles > 0)
                                {
                                    dirs.Add(node.Name, node);
                                }
                            }
                        }
                    }

                    foreach (var pattern in Properties.GetFilePatternEnumerable())
                    {
                        if (pattern != null)
                        {
                            foreach (var item in dirInfo.EnumerateFiles(pattern, EnumerateOptions))
                            {
                                if (!files.ContainsKey(item.Name))
                                {
                                    var node = new NodeItem(item, this);
                                    node.Refresh();
                                    files.Add(node.Name, node);
                                }
                            }
                        }
                    }

                    _contents.Clear();
                    _contents.AddRange(dirs.Values);
                    _contents.AddRange(files.Values);

                    // Detect changes and count files
                    int count = 0;
                    int code = base.GetHashCode();

                    foreach (var item in _contents)
                    {
                        count += item.TotalFiles;
                        code = HashCode.Combine(code, item.GetHashCode());

                        if (item.LastUtc > _lastUtc)
                        {
                            _lastUtc = item.LastUtc;
                        }
                    }

                    TotalFiles = count;
                    changed |= code != _hashCode;
                    _hashCode = code;
                    _lastPattern = Properties.FilePatterns;
                }
                catch (IOException e)
                {
                    Debug.WriteLine("Error in " + nameof(Refresh) + " method");
                    Debug.WriteLine(e);
                }
            }

            return changed;
        }

        /// <summary>
        /// Overrides to extend code to include changes to <see cref="Contents"/> and properties.
        /// </summary>
        public override int GetHashCode()
        {
            return _hashCode;
        }

        /// <summary>
        /// Overrides and returns a multi-line hierarchy if verbose is true.
        /// </summary>
        public override string ToString(bool verbose)
        {
            if (verbose)
            {
                var sb = new StringBuilder();
                ToString(sb, 0);
                return sb.ToString();
            }

            return ToString();
        }

        private void ToString(StringBuilder sb, int indent)
        {
            sb.Append(new string(' ', indent));
            sb.AppendLine(Name);

            if (_contents != null)
            {
                foreach (var item in _contents)
                {
                    item.ToString(sb, indent + 2);
                }
            }
        }

        private NodeItem? FindInternal(string name, bool? isFile, StringComparison comparison)
        {
            if (isFile == null && FullName.Equals(name, comparison))
            {
                return this;
            }

            if (isFile != null && IsDirectory != isFile && Name.Equals(name, comparison))
            {
                return this;
            }

            if (_contents != null)
            {
                for (int n = _contents.Count - 1; n > -1; --n)
                {
                    var found = _contents[n].FindInternal(name, isFile, comparison);

                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

    }
}