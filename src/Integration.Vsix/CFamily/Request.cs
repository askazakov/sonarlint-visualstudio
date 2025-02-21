﻿/*
 * SonarLint for Visual Studio
 * Copyright (C) 2016-2022 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using SonarLint.VisualStudio.Core.CFamily;
using SonarLint.VisualStudio.Integration.Vsix.CFamily.VcxProject;

namespace SonarLint.VisualStudio.Integration.Vsix.CFamily
{
    // Most of the Request class is ported from Java - see PortedFromJava\Request.cs
    internal partial class Request : IRequest
    {
        public RequestContext Context { get; set; }

        /// <summary>
        /// This protocol does not use environment variables
        /// </summary>
        public IReadOnlyDictionary<string, string> EnvironmentVariables => null;

        // TODO - duplicate property - remove
        internal string CFamilyLanguage { get; set; }

        internal IFileConfig FileConfig { get; set; }

        public void WriteRequest(BinaryWriter writer) =>
            Protocol.Write(writer, this);

        public void WriteRequestDiagnostics(TextWriter writer)
        {
            var serializedFileConfig = JsonConvert.SerializeObject(FileConfig);
            writer.Write(serializedFileConfig);
        }
    }
}
