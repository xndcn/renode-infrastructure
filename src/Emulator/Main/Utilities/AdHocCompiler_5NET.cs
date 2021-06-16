//
// Copyright (c) 2010-2021 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.IO;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Exceptions;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Antmicro.Renode.Utilities
{
    public class AdHocCompiler
    {
        public string Compile(string sourcePath)
        {
            var tempFilePath = TemporaryFilesManager.Instance.GetTemporaryFile();
            // With .NET 5+ and .NET Core, one must explicitly specify a .dll extension for output assembly
            var outputFilePath = Path.ChangeExtension(tempFilePath, ".dll");
            var outputFileName = Path.GetFileName(outputFilePath);

            var sourceCode = File.ReadAllText(sourcePath);
            var codeString = SourceText.From(sourceCode);
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9);

            var parsedSyntaxTree = SyntaxFactory.ParseSyntaxTree(codeString, options);

            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            };
            
            Assembly.GetEntryAssembly()?.GetReferencedAssemblies().ToList()
                .ForEach(a => references.Add(MetadataReference.CreateFromFile(Assembly.Load(a).Location)));

            AssemblyHelper.GetAssembliesLocations().ToList()
                .ForEach(location => references.Add(MetadataReference.CreateFromFile(location)));

            var result = CSharpCompilation.Create(outputFileName,
                new[] { parsedSyntaxTree }, 
                references: references, 
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, 
                    optimizationLevel: OptimizationLevel.Release,
                    assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default)).Emit(outputFilePath);

            if (!result.Success) 
            {
                // One have access to diagnostic informations that can be print 
                // var failures = result.Diagnostics.Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error);
                throw new RecoverableException($"Could not compile assembly from: {sourcePath}");      
            }

            return outputFilePath;
        }
    }
}