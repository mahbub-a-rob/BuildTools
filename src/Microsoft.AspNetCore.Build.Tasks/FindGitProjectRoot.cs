// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.AspNetCore.Build.Tasks
{
    public class FindGitProjectRoot : Task
    {
        [Required]
        public string StartDirectory { get; set; }

        [Output]
        public string RootDirectory { get; set; }

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(StartDirectory))
            {
                Log.LogError($"StartDirectory must be not null or empty");
                return false;
            }

            var dirInfo = new DirectoryInfo(StartDirectory);

            if (dirInfo.Exists)
            {
                Log.LogError($"Specified start directory does not exist: '{StartDirectory}'");
                return false;
            }

            while (dirInfo != null)
            {
                var gitDir = dirInfo.GetFiles(".git").FirstOrDefault();
                if (gitDir != null && gitDir.Exists && (gitDir.Attributes & FileAttributes.Directory) != 0)
                {
                    // return the folder containing .git, not the .git folder itself
                    RootDirectory = dirInfo.FullName;
                    return true;
                }
                dirInfo = dirInfo.Parent;
            }
            Log.LogError($"Could not find a git directory for '{StartDirectory}'");
            return false;
        }
    }
}