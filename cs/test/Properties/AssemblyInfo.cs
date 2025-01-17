﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using NUnit.Framework;
using System.Reflection;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
//[assembly: AssemblyTitle("FASTER.test")]
[assembly: AssemblyDescription("")]
//[assembly: AssemblyConfiguration("")]
//[assembly: AssemblyCompany("")]
//[assembly: AssemblyProduct("FASTER.test")]
[assembly: AssemblyCopyright("Copyright ©  2016")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("7204b801-fb6f-465b-858f-f6493fe7aaf7")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers 
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
//[assembly: AssemblyVersion("1.0.0.0")]
//[assembly: AssemblyFileVersion("1.0.0.0")]

// Make all fixtures in the test assembly run in parallel
#if false // disable parallelism until all the problems are resolved
//#if NETCOREAPP || NET    // net461 runs x86 by default so OOMs on current memory usage by tests when running multiple tests simultaneously
[assembly: Parallelizable(ParallelScope.Fixtures)]
    //[assembly: LevelOfParallelism(4)] // For reduced parallelization of net461 if we reduce memory usage in tests
#endif
