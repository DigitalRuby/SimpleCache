global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Runtime.CompilerServices;
global using System.Reflection;
global using System.Text;

global using Microsoft.Extensions.Caching.Distributed;
global using Microsoft.Extensions.Caching.Memory;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.DependencyInjection.Extensions;
global using Microsoft.Extensions.Internal;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Options;

global using Blake2Fast;

global using K4os.Compression.LZ4.Streams;

global using Polly;
global using Polly.Contrib.DuplicateRequestCollapser;
global using Polly.Wrap;

global using StackExchange.Redis;

namespace DigitalRuby.SimpleCache;