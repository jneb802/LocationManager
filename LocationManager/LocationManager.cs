﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using SoftReferenceableAssets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocationManager;

public enum ShowIcon
{
	Always,
	Never,
	Explored,
}

public enum Rotation
{
	Fixed,
	Random,
	Slope,
}

[PublicAPI]
public struct Range
{
	public float min;
	public float max;

	public Range(float min, float max)
	{
		this.min = min;
		this.max = max;
	}
}

[PublicAPI]
public class Location
{
	public bool CanSpawn = true;
	public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
	[Description("If the location should spawn more towards the edge of the biome or towards the center.\nUse 'Edge' to make it spawn towards the edge.\nUse 'Median' to make it spawn towards the center.\nUse 'Everything' if it doesn't matter.")]
	public Heightmap.BiomeArea SpawnArea = Heightmap.BiomeArea.Everything;
	[Description("Maximum number of locations to spawn in.\nDoes not mean that this many locations will spawn. But Valheim will try its best to spawn this many, if there is space.")]
	public int Count = 1;
	[Description("If set to true, this location will be prioritized over other locations, if they would spawn in the same area.")]
	public bool Prioritize = false;
	[Description("If set to true, Valheim will try to spawn your location as close to the center of the map as possible.")]
	public bool PreferCenter = false;
	[Description("If set to true, all other locations will be deleted, once the first one has been discovered by a player.")]
	public bool Unique = false;
	[Description("The name of the group of the location, used by the minimum distance from group setting.")]
	public string GroupName;
	[Description("Locations in the same group will keep at least this much distance between each other.")]
	public float MinimumDistanceFromGroup = 0f;
	[Description("When to show the map icon of the location. Requires an icon to be set.\nUse 'Never' to not show a map icon for the location.\nUse 'Always' to always show a map icon for the location.\nUse 'Explored' to start showing a map icon for the location as soon as a player has explored the area.")]
	public ShowIcon ShowMapIcon = ShowIcon.Never;

	public readonly GameObject Prefab;

	[Description("Sets the map icon for the location.")]
	public string? MapIcon
	{
		get => mapIconName;
		set
		{
			mapIconName = value;
			MapIconSprite = mapIconName is null ? null : loadSprite(mapIconName);
		}
	}

	private string? mapIconName = null;
	[Description("Sets the map icon for the location.")]
	public Sprite? MapIconSprite = null;
	[Description("How to rotate the location.\nUse 'Fixed' to use the rotation of the prefab.\nUse 'Random' to randomize the rotation.\nUse 'Slope' to rotate the location along a possible slope.")]
	public Rotation Rotation = Rotation.Random;
	[Description("The minimum and maximum height difference of the terrain below the location.")]
	public Range HeightDelta = new(0, 2);
	[Description("If the location should spawn near water.")]
	public bool SnapToWater = false;
	[Description("If the location should spawn in a forest.\nEverything above 1.15 is considered a forest by Valheim.\n2.19 is considered a thick forest by Valheim.")]
	public Range ForestThreshold = new(0, 2.19f);
	[Description("Minimum and maximum range from the center of the map for the location.")]
	public Range SpawnDistance = new(0, 10000);
	[Description("Minimum and maximum altitude for the location.")]
	public Range SpawnAltitude = new(-1000f, 1000f);
	[Description("If set to true, vegetation is removed inside the location exterior radius.")]
	public bool ClearArea = false;
	[Description("Adds a creature to a spawner that has been added to the location prefab.")]
	public Dictionary<string, string> CreatureSpawner = new();

	public static bool ConfigurationEnabled = true;

	private readonly global::Location location;
	private string folderName = "";
	private AssetBundle? assetBundle;
	private static readonly List<Location> registeredLocations = new();
	private static Dictionary<Location, SoftReference<GameObject>>? softReferences;
	
	public Location(string assetBundleFileName, string prefabName, string folderName = "assets") : this(PrefabManager.RegisterAssetBundle(assetBundleFileName, folderName), prefabName)
	{
		this.folderName = folderName;
	}

	public Location(AssetBundle bundle, string prefabName) : this(bundle.LoadAsset<GameObject>(prefabName))
	{
		assetBundle = bundle;
	}

	public Location(GameObject location) : this(location.GetComponent<global::Location>())
	{
		if (this.location == null)
		{
			throw new ArgumentNullException(nameof(location), "The GameObject does not have a location component.");
		}
	}

	public Location(global::Location location)
	{
		this.location = location;
        Prefab = this.location.gameObject;
		GroupName = location.name;
		registeredLocations.Add(this);
	}

	private byte[]? ReadEmbeddedFileBytes(string name)
	{
		using MemoryStream stream = new();
		if (Assembly.GetExecutingAssembly().GetManifestResourceStream(Assembly.GetExecutingAssembly().GetName().Name + $"{(folderName == "" ? "" : ".") + folderName}." + name) is not { } assemblyStream)
		{
			return null;
		}

		assemblyStream.CopyTo(stream);
		return stream.ToArray();
	}

    private Texture2D? loadTexture(string name)
    {
        try
        {
            if (ReadEmbeddedFileBytes(name) is { } textureData)
            {
                Texture2D texture = new(0, 0);
                texture.LoadImage(textureData);
                return texture;
            }
            return null;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load texture '{name}': {e.Message}");
            return null;
        }
    }

    private Sprite loadSprite(string name)
    {
        try
        {
            if (loadTexture(name) is { } texture)
            {
                return Sprite.Create(texture, new Rect(0, 0, 64, 64), Vector2.zero);
            }
            if (assetBundle?.LoadAsset<Sprite>(name) is { } sprite)
            {
                return sprite;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load sprite '{name}': {e.Message}");
        }

		throw new FileNotFoundException($"Could not find a file named {name} for the map icon");
	}

	private static void AddLocationToZoneSystem(ZoneSystem __instance)
	{
		softReferences ??= registeredLocations.ToDictionary(l => l, l => PrefabManager.AddLoadedSoftReferenceAsset(l.location.gameObject));
		
		foreach (Location location in registeredLocations)
		{
			__instance.m_locations.Add(new ZoneSystem.ZoneLocation
			{
				m_prefabName = location.location.name,
				m_prefab = softReferences[location],
				m_enable = location.CanSpawn,
				m_biome = location.Biome,
				m_biomeArea = location.SpawnArea,
				m_quantity = location.Count,
				m_prioritized = location.Prioritize,
				m_centerFirst = location.PreferCenter,
				m_unique = location.Unique,
				m_group = location.GroupName,
				m_minDistanceFromSimilar = location.MinimumDistanceFromGroup,
				m_iconAlways = location.ShowMapIcon == ShowIcon.Always,
				m_iconPlaced = location.ShowMapIcon == ShowIcon.Explored,
				m_randomRotation = location.Rotation == Rotation.Random,
				m_slopeRotation = location.Rotation == Rotation.Slope,
				m_snapToWater = location.SnapToWater,
				m_minTerrainDelta = location.HeightDelta.min,
				m_maxTerrainDelta = location.HeightDelta.max,
				m_inForest = true,
				m_forestTresholdMin = location.ForestThreshold.min,
				m_forestTresholdMax = location.ForestThreshold.max,
				m_minDistance = location.SpawnDistance.min,
				m_maxDistance = location.SpawnDistance.max,
				m_minAltitude = location.SpawnAltitude.min,
				m_maxAltitude = location.SpawnAltitude.max,
				m_clearArea = location.ClearArea,
				m_exteriorRadius = location.location.m_exteriorRadius,
				m_interiorRadius = location.location.m_interiorRadius,
			});
		}

		Object.DestroyImmediate(__instance.m_locationProxyPrefab.GetComponent<LocationProxy>());
		__instance.m_locationProxyPrefab.AddComponent<LocationProxy>();
	}

	private static void AddLocationZNetViewsToZNetScene(ZNetScene __instance)
	{
		foreach (ZNetView netView in registeredLocations.SelectMany(l => l.location.GetComponentsInChildren<ZNetView>(true)))
		{
			if (__instance.m_namedPrefabs.ContainsKey(netView.GetPrefabName().GetStableHashCode()))
			{
				string otherName = Utils.GetPrefabName(__instance.m_namedPrefabs[netView.GetPrefabName().GetStableHashCode()]);
				if (netView.GetPrefabName() != otherName)
				{
					Debug.LogError($"Found hash collision for names of prefabs {netView.GetPrefabName()} and {otherName} in {Assembly.GetExecutingAssembly()}. Skipping.");
				}
			}
			else
			{
				__instance.m_prefabs.Add(netView.gameObject);
				__instance.m_namedPrefabs[netView.GetPrefabName().GetStableHashCode()] = netView.gameObject;
			}
		}

		foreach (Location location in registeredLocations)
		{
			foreach (CreatureSpawner spawner in location.location.transform.GetComponentsInChildren<CreatureSpawner>())
			{
				if (location.CreatureSpawner.TryGetValue(spawner.name, out string creature))
				{
					spawner.m_creaturePrefab = __instance.GetPrefab(creature);
				}
			}
		}
	}

	private static void AddMinimapIcons(Minimap __instance)
	{
		foreach (Location location in registeredLocations)
		{
			if (location.MapIconSprite is { } icon)
			{
				__instance.m_locationIcons.Add(new Minimap.LocationSpriteData { m_icon = icon, m_name = location.location.name });
			}
		}
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public int? Order;
	}

	private static bool firstStartup = true;

	internal static void Patch_FejdStartup()
	{
		if (ConfigurationEnabled && firstStartup)
		{
			bool SaveOnConfigSet = plugin.Config.SaveOnConfigSet;
			plugin.Config.SaveOnConfigSet = false;

			foreach (Location location in registeredLocations)
			{
				int order = 0;
				foreach (KeyValuePair<string, string> kv in location.CreatureSpawner)
				{
					ConfigEntry<string> spawnerCreature = config(location.location.name, $"{kv.Key} spawns", kv.Value, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = --order }));
					spawnerCreature.SettingChanged += (_, _) =>
					{
						location.CreatureSpawner[kv.Key] = spawnerCreature.Value;
						if (ZNetScene.instance && location.location.transform.GetComponentsInChildren<CreatureSpawner>().FirstOrDefault(s => s.name == kv.Key) is { } spawner)
						{
							spawner.m_creaturePrefab = ZNetScene.instance.GetPrefab(spawnerCreature.Value);
						}
					};
				}
			}

			if (SaveOnConfigSet)
			{
				plugin.Config.SaveOnConfigSet = true;
				plugin.Config.Save();
			}
		}
		firstStartup = false;
	}

	static Location()
	{
		Harmony harmony = new("org.bepinex.helpers.LocationManager");
		harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), nameof(ZNetScene.Awake)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Location), nameof(AddLocationZNetViewsToZNetScene)), Priority.VeryLow));
		harmony.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.SetupLocations)), new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Location), nameof(AddLocationToZoneSystem))));
		harmony.Patch(AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.Awake)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Location), nameof(AddMinimapIcons))));
		harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(FejdStartup.Awake)), new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Location), nameof(Patch_FejdStartup))));
	}

public static class PrefabManager
{
    private struct BundleId
    {
        [UsedImplicitly]
        public string assetBundleFileName;
        [UsedImplicitly]
        public string folderName;
    }

    private static readonly Dictionary<BundleId, AssetBundle> bundleCache = new();

    public static AssetBundle RegisterAssetBundle(string assetBundleFileName, string folderName = "assets")
    {
        BundleId id = new() { assetBundleFileName = assetBundleFileName, folderName = folderName };
        if (bundleCache.TryGetValue(id, out AssetBundle existingBundle))
        {
            Debug.LogWarning($"AssetBundle {assetBundleFileName} in folder {folderName} is already loaded.");
            return existingBundle;
        }

        AssetBundle assets;
        try
        {
            assets = Resources.FindObjectsOfTypeAll<AssetBundle>().FirstOrDefault(a => a.name == assetBundleFileName) ??
                     AssetBundle.LoadFromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream(Assembly.GetExecutingAssembly().GetName().Name + $"{(folderName == "" ? "" : ".") + folderName}." + assetBundleFileName));

            if (assets == null)
            {
                throw new FileNotFoundException($"AssetBundle {assetBundleFileName} not found in folder {folderName}.");
            }

            bundleCache[id] = assets;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load AssetBundle {assetBundleFileName} in folder {folderName}: {e.Message}");
            throw;
        }
        return assets;
    }

		[PublicAPI]
		public static AssetID AssetIDFromObject(Object obj)
		{
			int id = obj.GetInstanceID();
			return new AssetID(1, 1, 1, (uint)id);
		}

		public static SoftReference<T> AddLoadedSoftReferenceAsset<T>(T obj) where T: Object
		{
			AssetBundleLoader bundleLoader = AssetBundleLoader.Instance;
			bundleLoader.m_bundleNameToLoaderIndex[""] = 0; // So that AssetLoader ctor doesn't crash
			
			AssetID id = AssetIDFromObject(obj);

			// Ensure the path is unique by appending a unique identifier if necessary
			string uniquePath = obj.name;
			int counter = 1;
			while (bundleLoader.m_assetIDToLoaderIndex.ContainsKey(id))
			{
				uniquePath = obj.name + "_" + counter;
				counter++;
			}

			AssetLoader loader = new(id, new AssetLocation("", uniquePath))
			{
				m_asset = obj,
				m_referenceCounter = new ReferenceCounter(2),
				m_shouldBeLoaded = true,
			};

			int count = bundleLoader.m_assetIDToLoaderIndex.Count;
			if (count >= bundleLoader.m_assetLoaders.Length)
			{
				Array.Resize(ref bundleLoader.m_assetLoaders, count + registeredLocations.Count);
			}

			bundleLoader.m_assetLoaders[count] = loader;
			bundleLoader.m_assetIDToLoaderIndex[id] = count; 

			return new SoftReference<T>(id) { m_name = obj.name };
		}
	}
	
	private static BaseUnityPlugin? _plugin;

	private static BaseUnityPlugin plugin
	{
		get
		{
			if (_plugin is null)
			{
				IEnumerable<TypeInfo> types;
				try
				{
					types = Assembly.GetExecutingAssembly().DefinedTypes.ToList();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types.Where(t => t != null).Select(t => t.GetTypeInfo());
				}
				_plugin = (BaseUnityPlugin)BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent(types.First(t => t.IsClass && typeof(BaseUnityPlugin).IsAssignableFrom(t)));
			}
			return _plugin;
		}
	}

	private static bool hasConfigSync = true;
	private static object? _configSync;

	private static object? configSync
	{
		get
		{
			if (_configSync == null && hasConfigSync)
			{
				if (Assembly.GetExecutingAssembly().GetType("ServerSync.ConfigSync") is { } configSyncType)
				{
					_configSync = Activator.CreateInstance(configSyncType, plugin.Info.Metadata.GUID + " ItemManager");
					configSyncType.GetField("CurrentVersion").SetValue(_configSync, plugin.Info.Metadata.Version.ToString());
					configSyncType.GetProperty("IsLocked")!.SetValue(_configSync, true);
				}
				else
				{
					hasConfigSync = false;
				}
			}

			return _configSync;
		}
	}

	private static ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
	{
		ConfigEntry<T> configEntry = plugin.Config.Bind(group, name, value, description);

		configSync?.GetType().GetMethod("AddConfigEntry")!.MakeGenericMethod(typeof(T)).Invoke(configSync, new object[] { configEntry });

		return configEntry;
	}

	private static ConfigEntry<T> config<T>(string group, string name, T value, string description) => config(group, name, value, new ConfigDescription(description));
}