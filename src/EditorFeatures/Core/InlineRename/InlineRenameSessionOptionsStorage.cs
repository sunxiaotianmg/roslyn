﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.InlineRename
{
    internal static class InlineRenameSessionOptionsStorage
    {
        private const string FeatureName = "InlineRenameSessionOptions";

        public static readonly Option2<bool> RenameOverloads = new(FeatureName, "RenameOverloads", defaultValue: false);
        public static readonly Option2<bool> RenameInStrings = new(FeatureName, "RenameInStrings", defaultValue: false);
        public static readonly Option2<bool> RenameInComments = new(FeatureName, "RenameInComments", defaultValue: false);
        public static readonly Option2<bool> RenameFile = new(FeatureName, "RenameFile", defaultValue: true);
        public static readonly Option2<bool> PreviewChanges = new(FeatureName, "PreviewChanges", defaultValue: false);
        public static readonly Option2<bool> RenameAsynchronously = new(FeatureName, "RenameAsynchronously", defaultValue: true);
    }
}
