using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System;
using UnityEngine;

using CameraTools.Utils;

namespace CameraTools.ModIntegration
{
	[KSPAddon(KSPAddon.Startup.Flight, false)]
	public class BDArmory : MonoBehaviour
	{
		#region Public fields
		public static BDArmory instance;
		public static bool hasBDA = false;

		[CTPersistantField] public bool autoEnableForBDA = true;
		[CTPersistantField] public bool useBDAutoTarget = true;
		[CTPersistantField] public bool autoTargetIncomingMissiles = false;
		[CTPersistantField] public bool useCentroid = false;
		public bool autoEnableOverride = false;
		public bool hasBDAI = false;
		public bool hasPilotAI = false;
		public AIType aiType = AIType.Unknown;
		public enum AIType { Pilot, Surface, VTOL, Orbital, Unknown };
		public bool isBDMissile = false;
		public List<Vessel> bdWMVessels
		{
			get
			{
				if (hasBDA && (_bdWMVessels is null || Time.time - _bdWMVesselsLastUpdate > 1f)) GetBDVessels(); // Update once per second.
				return _bdWMVessels;
			}
		}
		public float restoreDistanceLimit = float.MaxValue; // Limit the distance to restore the camera to (for auto-enabling).
		#endregion

		#region Private fields
		CamTools camTools => CamTools.fetch;
		Vessel vessel => CamTools.fetch.vessel;
		public Dictionary<string, FloatInputField> inputFields;
		object bdCompetitionInstance = null;
		Func<object, bool> bdCompetitionStartingFieldGetter = null;
		Func<object, bool> bdCompetitionIsActiveFieldGetter = null;
		object bdVesselSpawnerInstance = null;
		Func<object, bool> bdVesselsSpawningFieldGetter = null;
		Func<object, object> bdVesselsSpawningPropertyGetter = null;
		Func<object, object> bdInhibitCameraToolsPropertyGetter = null;
		Func<object, object> bdRestoreDistanceLimitPropertyGetter = null;
		Func<object, object> bdMissileTargetVesselPropertyGetter = null;
		Func<object, object> bdMissileTargetPositionPropertyGetter = null;
		Type bdBDATournamentType = null;
		object bdBDATournamentInstance = null;
		Func<object, bool> bdTournamentWarpInProgressFieldGetter = null;
		bool hasBDWM = false;
		Type aiModType = null;
		object aiComponent = null;
		Func<object, Vessel> bdAITargetFieldGetter = null;
		Type wmModType = null;
		object wmComponent = null;
		Func<object, Vessel> bdWmThreatFieldGetter = null;
		Func<object, Vessel> bdWmMissileFieldGetter = null;
		Func<object, bool> bdWmUnderFireFieldGetter = null;
		Func<object, bool> bdWmUnderAttackFieldGetter = null;
		Func<object, object> bdWmRecentlyFiringPropertyGetter = null;
		object bdLoadedVesselSwitcherInstance = null;
		Func<object, object> bdLoadedVesselSwitcherVesselsPropertyGetter = null;
		Dictionary<string, List<Vessel>> bdActiveVessels = new Dictionary<string, List<Vessel>>();
		float AItargetUpdateTime = 0;
		[CTPersistantField] public float AItargetMinimumUpdateInterval = 3;
		[CTPersistantField] public float AItargetMinimumMissileUpdateInterval = 0.5f;
		[CTPersistantField] public float AItargetSecondaryTargetDeathSwitchDelay = 2;
		float AItargetVesselLastAliveTime = 0;
		Vessel newAITarget = null;
		public bool aiTargetIsMissile = false;
		List<Vessel> _bdWMVessels = new List<Vessel>();
		float _bdWMVesselsLastUpdate = 0;
		#endregion

		void Awake()
		{
			if (instance is not null) Destroy(instance);
			instance = this;
			CTPersistantField.Load("BDArmoryIntegration", typeof(BDArmory), this);
		}

		void Start()
		{
			CheckForBDA();
			if (!hasBDA)
			{
				Destroy(this);
				return;
			}
			if (hasBDA)
			{
				aiModType = GetAIModuleType();
				wmModType = GetWeaponManagerType();
				GetAITargetField();
				GetThreatField();
				GetMissileField();
				GetUnderFireField();
				GetUnderAttackField();
				GetRecentlyFiringProperty();
				if (FlightGlobals.ActiveVessel is not null)
				{
					CheckForBDAI(FlightGlobals.ActiveVessel);
					CheckForBDWM(FlightGlobals.ActiveVessel);
					if (!hasBDAI) CheckForBDMissile(FlightGlobals.ActiveVessel);
				}
			}
			inputFields = new Dictionary<string, FloatInputField> {
				{"AItargetMinimumUpdateInterval", gameObject.AddComponent<FloatInputField>().Initialise(0, AItargetMinimumUpdateInterval, 0.5f, 5, 4)},
				{"AItargetMinimumMissileUpdateInterval", gameObject.AddComponent<FloatInputField>().Initialise(0, AItargetMinimumMissileUpdateInterval, 0, 1, 4)},
				{"AItargetSecondaryTargetDeathSwitchDelay", gameObject.AddComponent<FloatInputField>().Initialise(0, AItargetSecondaryTargetDeathSwitchDelay, 0, 5, 4)},
			};
		}

		void OnDestroy()
		{
			CTPersistantField.Save("BDArmoryIntegration", typeof(BDArmory), this);
		}

		void FixedUpdate()
		{
			_inhibitChecked = false;
		}

		void CheckForBDA()
		{
			// This checks for the existence of a BDArmory assembly and picks out the BDACompetitionMode and VesselSpawner singletons.
			bdCompetitionInstance = null;
			bdCompetitionIsActiveFieldGetter = null;
			bdCompetitionStartingFieldGetter = null;
			bdVesselSpawnerInstance = null;
			bdVesselsSpawningFieldGetter = null;
			bdVesselsSpawningPropertyGetter = null;
			bdInhibitCameraToolsPropertyGetter = null;
			bdRestoreDistanceLimitPropertyGetter = null;
			bdMissileTargetVesselPropertyGetter = null;
			bdMissileTargetPositionPropertyGetter = null;
			bdLoadedVesselSwitcherVesselsPropertyGetter = null;
			bdBDATournamentType = null;
			bdBDATournamentInstance = null;
			foreach (var assy in AssemblyLoader.loadedAssemblies)
			{
				if (assy.assembly.FullName.Contains("BDArmory"))
				{
					hasBDA = true;
					foreach (var t in assy.assembly.GetTypes())
					{
						if (t != null)
						{
							switch (t.Name)
							{
								case "BDACompetitionMode":
									bdCompetitionInstance = FindObjectOfType(t);
									foreach (var fieldInfo in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
										if (fieldInfo != null)
										{
											switch (fieldInfo.Name)
											{
												case "competitionStarting":
													bdCompetitionStartingFieldGetter = ReflectionUtils.CreateGetter<object, bool>(fieldInfo);
													break;
												case "competitionIsActive":
													bdCompetitionIsActiveFieldGetter = ReflectionUtils.CreateGetter<object, bool>(fieldInfo);
													break;
												default:
													break;
											}
										}
									break;
								case "VesselSpawnerStatus":
									if (bdInhibitCameraToolsPropertyGetter == null) // Skip if we've found a more up-to-date property to access.
									{
										foreach (var propertyInfo in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
											if (propertyInfo != null && propertyInfo.Name == "inhibitCameraTools")
											{
												bdVesselsSpawningPropertyGetter = ReflectionUtils.BuildGetAccessor(propertyInfo.GetGetMethod());
												if (bdVesselsSpawningFieldGetter != null) // Clear the deprecated field.
												{ bdVesselsSpawningFieldGetter = null; }
												break;
											}
									}
									break;
								case "LoadedVesselSwitcher":
									bdLoadedVesselSwitcherInstance = FindObjectOfType(t);
									foreach (var propertyInfo in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
										if (propertyInfo != null && propertyInfo.Name == "Vessels")
										{
											bdLoadedVesselSwitcherVesselsPropertyGetter = ReflectionUtils.BuildGetAccessor(propertyInfo.GetGetMethod());
											break;
										}
									break;
								case "VesselSpawner":
									if (bdVesselsSpawningPropertyGetter == null && bdInhibitCameraToolsPropertyGetter == null) // Skip if we've found a more up-to-date property to access.
									{
										if (!t.IsSubclassOf(typeof(UnityEngine.Object))) continue; // In BDArmory v1.5.0 and upwards, VesselSpawner is a static class.
										bdVesselSpawnerInstance = FindObjectOfType(t);
										foreach (var fieldInfo in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
											if (fieldInfo != null && fieldInfo.Name == "vesselsSpawning") // deprecated in favour of VesselSpawnerStatus.inhibitCameraTools
											{
												bdVesselsSpawningFieldGetter = ReflectionUtils.CreateGetter<object, bool>(fieldInfo);
												break;
											}
									}
									break;
								case "CameraTools":
									foreach (var propertyInfo in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
										if (propertyInfo != null)
										{
											switch (propertyInfo.Name)
											{
												case "InhibitCameraTools":
													bdInhibitCameraToolsPropertyGetter = ReflectionUtils.BuildGetAccessor(propertyInfo.GetGetMethod());
													// Clear the deprecated fields.
													bdVesselsSpawningFieldGetter = null;
													bdVesselsSpawningPropertyGetter = null;
													break;
												case "RestoreDistanceLimit":
													bdRestoreDistanceLimitPropertyGetter = ReflectionUtils.BuildGetAccessor(propertyInfo.GetGetMethod());
													break;
												case "MissileTargetVessel":
													bdMissileTargetVesselPropertyGetter = ReflectionUtils.BuildGetAccessor(propertyInfo.GetGetMethod());
													break;
												case "MissileTargetPosition":
													bdMissileTargetPositionPropertyGetter = ReflectionUtils.BuildGetAccessor(propertyInfo.GetGetMethod());
													break;
											}
										}
									break;
								case "BDATournament":
									bdBDATournamentType = t;
									bdBDATournamentInstance = FindObjectOfType(bdBDATournamentType);
									foreach (var fieldInfo in bdBDATournamentType.GetFields(BindingFlags.Public | BindingFlags.Instance))
										if (fieldInfo != null && fieldInfo.Name == "warpingInProgress")
										{
											bdTournamentWarpInProgressFieldGetter = ReflectionUtils.CreateGetter<object, bool>(fieldInfo);
											break;
										}
									break;
								default:
									break;
							}
						}
					}
				}
			}
		}

		public void CheckForBDAI(Vessel v)
		{
			hasBDAI = false;
			hasPilotAI = false;
			aiType = AIType.Unknown;
			isBDMissile = false;
			aiComponent = null;
			if (v)
			{
				foreach (Part p in v.parts)
				{ // We actually want BDGenericAIBase, but we can't use GetComponent(string) to find it (since it's abstract), so we look for its subclasses.
					if (p.GetComponent("BDModulePilotAI"))
					{
						hasBDAI = true;
						hasPilotAI = true;
						aiType = AIType.Pilot;
						aiComponent = (object)p.GetComponent("BDModulePilotAI");
						return;
					}
					if (p.GetComponent("BDModuleVTOLAI"))
					{
						hasBDAI = true;
						hasPilotAI = true;
						aiType = AIType.VTOL;
						aiComponent = (object)p.GetComponent("BDModuleVTOLAI");
						return;
					}
					if (p.GetComponent("BDModuleSurfaceAI"))
					{
						hasBDAI = true;
						hasPilotAI = false;
						aiType = AIType.Surface;
						aiComponent = (object)p.GetComponent("BDModuleSurfaceAI");
						return;
					}
					if (p.GetComponent("BDModuleOrbitalAI"))
					{
						hasBDAI = true;
						hasPilotAI = true;
						aiType = AIType.Orbital;
						aiComponent = (object)p.GetComponent("BDModuleOrbitalAI");
						return;
					}
				}
			}
		}

		public void CheckForBDMissile(Vessel v)
		{
			isBDMissile = false;
			if (hasBDAI || hasBDWM) return; // If it has a WM or AI, then it's not a missile and we shouldn't even be checking.
			if (v == null) return; // If the vessel is null, it's not a missile.
			foreach (var p in v.parts)
			{ // We actually want MissileBase, but we can't use GetComponent(string) to find it (since it's abstract), so we look for its subclasses.
				if (p.GetComponent("MissileLauncher") || p.GetComponent("BDModularGuidance"))
				{
					isBDMissile = true;
					AItargetUpdateTime = 0; // Reset the AI target update time so that it'll immediately check for a new target.
					camTools.dogfightTarget = null; // Also, clear the dogfight target so that the debug messages are correct.
					return;
				}
			}
		}

		public bool CheckForBDWM(Vessel v)
		{
			hasBDWM = false;
			wmComponent = null;
			if (v)
			{
				foreach (Part p in v.parts)
				{
					if (p.GetComponent("MissileFire"))
					{
						hasBDWM = true;
						wmComponent = (object)p.GetComponent("MissileFire");
						return true;
					}
				}
			}
			return false;
		}

		Vessel GetAITargetedVessel()
		{
			// Don't update too often unless there's no target.
			if (camTools.dogfightTarget != null && Time.time - AItargetUpdateTime < AItargetMinimumMissileUpdateInterval)
				return camTools.dogfightTarget;

			// Missiles are high priority.
			if (autoTargetIncomingMissiles && hasBDWM && wmComponent != null && bdWmMissileFieldGetter != null)
			{
				var missile = bdWmMissileFieldGetter(wmComponent); // Priority 1: incoming missiles.
				if (missile != null)
				{
					if (CamTools.DEBUG && missile != camTools.dogfightTarget) CamTools.DebugLog($"Incoming missile {missile.vesselName}");
					aiTargetIsMissile = true;
					return missile;
				}
			}
			aiTargetIsMissile = false;

			// Don't update too often unless there's no target.
			if (camTools.dogfightTarget != null && Time.time - AItargetUpdateTime < AItargetMinimumUpdateInterval)
				return camTools.dogfightTarget;

			// Don't switch secondary targets if the secondary target was a vessel that just died.
			if (Time.time - AItargetVesselLastAliveTime < AItargetSecondaryTargetDeathSwitchDelay && camTools.dogfightTarget == null)
				return null;

			// Recently firing on a target.
			Vessel target = null;
			if (hasBDAI && aiComponent != null && bdAITargetFieldGetter != null)
			{
				target = bdAITargetFieldGetter(aiComponent);
			}
			if (target != null && hasBDWM && wmComponent != null && bdWmRecentlyFiringPropertyGetter != null)
			{
				bool recentlyFiring = (bool)bdWmRecentlyFiringPropertyGetter(wmComponent); // Priority 2: recently firing on a target.
				if (recentlyFiring)
				{
					if (CamTools.DEBUG && target != camTools.dogfightTarget) CamTools.DebugLog($"Recently firing on {target.vesselName}");
					return target;
				}
			}

			// Threats
			if (hasBDWM && wmComponent != null && bdWmThreatFieldGetter != null)
			{
				bool underFire = bdWmUnderFireFieldGetter(wmComponent); // Getting attacked by guns.
				bool underAttack = autoTargetIncomingMissiles && bdWmUnderAttackFieldGetter(wmComponent); // Getting attacked by guns or missiles.

				if (underFire || underAttack)
				{
					var threat = bdWmThreatFieldGetter(wmComponent); // Priority 3: incoming fire (can also be missiles).
					if (threat != null)
					{
						if (CamTools.DEBUG && threat != camTools.dogfightTarget) CamTools.DebugLog($"Incoming fire/missile {threat.vesselName}");
						return threat;
					}
				}
			}

			// Targets
			if (target != null)
			{
				if (CamTools.DEBUG && target != camTools.dogfightTarget) CamTools.DebugLog($"Targeting {target.vesselName}");
				return target; // Priority 4: the current vessel's target.
			}
			return null;
		}

		Vessel GetMissileTargetedVessel()
		{
			if (!isBDMissile || bdMissileTargetVesselPropertyGetter == null) return null;
			return (Vessel)bdMissileTargetVesselPropertyGetter(null);
		}

		public Vector3 GetMissileTargetedPosition()
		{
			if (!isBDMissile || bdMissileTargetPositionPropertyGetter == null) return default;
			return (Vector3)bdMissileTargetPositionPropertyGetter(null);
		}

		Type GetAIModuleType()
		{
			foreach (var assy in AssemblyLoader.loadedAssemblies)
			{
				if (assy.assembly.FullName.Contains("BDArmory"))
				{
					foreach (var t in assy.assembly.GetTypes())
					{
						if (t.Name == "BDGenericAIBase")
						{
							if (CamTools.DEBUG) Debug.Log("[CameraTools.ModIntegration.BDArmory]: Found BDGenericAIBase type.");
							return t;
						}
					}
				}
			}

			return null;
		}

		Type GetWeaponManagerType()
		{
			foreach (var assy in AssemblyLoader.loadedAssemblies)
			{
				if (assy.assembly.FullName.Contains("BDArmory"))
				{
					foreach (var t in assy.assembly.GetTypes())
					{
						if (t.Name == "MissileFire")
						{
							if (CamTools.DEBUG) Debug.Log("[CameraTools.ModIntegration.BDArmory]: Found MissileFire type.");
							return t;
						}
					}
				}
			}

			return null;
		}

		public void UpdateAIDogfightTarget(bool checkSecondaryTarget = false)
		{
			if (checkSecondaryTarget) AItargetUpdateTime = 0; // Force a check for secondary targets.
			if (hasBDAI && hasBDWM && (useBDAutoTarget || (useCentroid && bdWMVessels.Count < 2)))
			{
				newAITarget = GetAITargetedVessel();
				if (newAITarget != null && !aiTargetIsMissile) AItargetVesselLastAliveTime = Time.time;
				if (newAITarget != camTools.dogfightTarget)
				{
					if (CamTools.DEBUG)
					{
						if (newAITarget != null) CamTools.DebugLog($"Switching dogfight target to {(newAITarget != null ? newAITarget.vesselName : "null")}{(camTools.dogfightTarget != null ? " from " + camTools.dogfightTarget.vesselName : "")}");
						else CamTools.DebugLog("No secondary targets available, using dogfight chase mode.");
					}
					camTools.dogfightTarget = newAITarget;
					AItargetUpdateTime = newAITarget != null ? Time.time : Time.time - AItargetMinimumUpdateInterval + AItargetMinimumMissileUpdateInterval;
				}
				else if (Time.time - AItargetUpdateTime >= AItargetMinimumUpdateInterval)
				{
					AItargetUpdateTime += Mathf.Max(0.5f, AItargetMinimumMissileUpdateInterval); // Delay the next update by 0.5s to avoid checking every frame once the minimum interval has elapsed.
				}
			}
			else if (isBDMissile && useBDAutoTarget)
			{
				if (Time.time - AItargetUpdateTime >= AItargetMinimumMissileUpdateInterval) // Only update the missile's target vessel at most every 0.5s (gets reset on vessel switches).
				{
					newAITarget = GetMissileTargetedVessel();
					if (CamTools.DEBUG)
					{
						if (newAITarget == null)
						{
							if (camTools.dogfightTarget)
							{
								var position = GetMissileTargetedPosition();
								if (position == default) CamTools.DebugLog("Missile has no target, using dogfight chase mode.");
								else CamTools.DebugLog($"Missile is targeting position {position}.");
							}
						}
						else if (newAITarget != camTools.dogfightTarget)
						{
							if (camTools.dogfightTarget) CamTools.DebugLog($"Missile switched target to {newAITarget.vesselName} from {camTools.dogfightTarget.vesselName}");
							else CamTools.DebugLog($"Missile is targeting {newAITarget.vesselName}");
						}
					}
					camTools.dogfightTarget = newAITarget;
					AItargetUpdateTime = Time.time;
				}
			}
		}

		public static bool IsInhibited => hasBDA && instance.isInhibited;
		bool isInhibited // Avoid checking across mods more than once per fixedUpdate.
		{
			get
			{
				if (!_inhibitChecked)
				{
					if (bdInhibitCameraToolsPropertyGetter != null) _inhibited = (bool)bdInhibitCameraToolsPropertyGetter(null);
					else if (bdVesselsSpawningPropertyGetter != null) _inhibited = (bool)bdVesselsSpawningPropertyGetter(null); // Deprecated in v1.6.9.0 of BDA.
					else if (bdVesselsSpawningFieldGetter != null) _inhibited = bdVesselsSpawningFieldGetter(bdVesselSpawnerInstance); // Deprecated, but just in case someone is using an older version of BDA.
					else _inhibited = false;
					_inhibitChecked = true;
				}
				return _inhibited;
			}
		}
		bool _inhibitChecked = false;
		bool _inhibited = false;

		public void AutoEnableForBDA()
		{
			if (!hasBDA) return;
			try
			{
				if (IsInhibited)
				{
					if (autoEnableOverride)
						return; // Still spawning.
					else
					{
						Debug.Log("[CameraTools.ModIntegration.BDArmory]: Deactivating CameraTools while spawning vessels.");
						autoEnableOverride = true;
						camTools.RevertCamera();
						return;
					}
				}

				if (bdTournamentWarpInProgressFieldGetter != null && bdTournamentWarpInProgressFieldGetter(bdBDATournamentInstance))
				{
					if (autoEnableOverride)
						return; // Still warping.
					else
					{
						Debug.Log("[CameraTools.ModIntegration.BDArmory]: Deactivating CameraTools while warping between rounds.");
						autoEnableOverride = true;
						camTools.RevertCamera();
						return;
					}
				}

				autoEnableOverride = false;
				if (camTools.cameraToolActive) return; // It's already active.

				if (vessel == null || (hasPilotAI && vessel.LandedOrSplashed)) return; // Don't activate for landed/splashed planes.
				if (bdCompetitionStartingFieldGetter != null && bdCompetitionStartingFieldGetter(bdCompetitionInstance))
				{
					Debug.Log("[CameraTools.ModIntegration.BDArmory]: Activating CameraTools for BDArmory competition as competition is starting.");
					camTools.CameraActivate();
					restoreDistanceLimit = bdRestoreDistanceLimitPropertyGetter != null ? (float)bdRestoreDistanceLimitPropertyGetter(null) : float.MaxValue;
					return;
				}
				else if (bdCompetitionIsActiveFieldGetter != null && bdCompetitionIsActiveFieldGetter(bdCompetitionInstance))
				{
					Debug.Log("[CameraTools.ModIntegration.BDArmory]: Activating CameraTools for BDArmory competition as competition is active.");
					UpdateAIDogfightTarget(true);
					camTools.CameraActivate();
					restoreDistanceLimit = bdRestoreDistanceLimitPropertyGetter != null ? (float)bdRestoreDistanceLimitPropertyGetter(null) : float.MaxValue;
					return;
				}
			}
			catch (Exception e)
			{
				Debug.LogError("[CameraTools.ModIntegration.BDArmory]: Checking competition state of BDArmory failed: " + e.Message);
				CheckForBDA();
			}
		}

		/// <summary>
		/// Perform any BDA-related actions when the camera is reverted.
		/// </summary>
		public void OnRevert()
		{
			restoreDistanceLimit = float.MaxValue; // Reset the restore distance limit for manual store/restore.
		}

		FieldInfo GetThreatField()
		{
			if (wmModType == null) return null;

			FieldInfo[] fields = wmModType.GetFields(BindingFlags.Public | BindingFlags.Instance);
			foreach (var f in fields)
			{
				if (f.Name == "incomingThreatVessel")
				{
					bdWmThreatFieldGetter = ReflectionUtils.CreateGetter<object, Vessel>(f);
					if (CamTools.DEBUG) Debug.Log($"[CameraTools.ModIntegration.BDArmory]: Created bdWmThreatFieldGetter.");
					return f;
				}
			}

			return null;
		}

		FieldInfo GetMissileField()
		{
			if (wmModType == null) return null;

			FieldInfo[] fields = wmModType.GetFields(BindingFlags.Public | BindingFlags.Instance);
			foreach (var f in fields)
			{
				if (f.Name == "incomingMissileVessel")
				{
					bdWmMissileFieldGetter = ReflectionUtils.CreateGetter<object, Vessel>(f);
					if (CamTools.DEBUG) Debug.Log($"[CameraTools.ModIntegration.BDArmory]: Created bdWmMissileFieldGetter.");
					return f;
				}
			}

			return null;
		}

		FieldInfo GetUnderFireField()
		{
			if (wmModType == null) return null;

			FieldInfo[] fields = wmModType.GetFields(BindingFlags.Public | BindingFlags.Instance);
			foreach (var f in fields)
			{
				if (f.Name == "underFire")
				{
					bdWmUnderFireFieldGetter = ReflectionUtils.CreateGetter<object, bool>(f);
					if (CamTools.DEBUG) Debug.Log($"[CameraTools.ModIntegration.BDArmory]: Created bdWmUnderFireFieldGetter.");
					return f;
				}
			}

			return null;
		}

		FieldInfo GetUnderAttackField()
		{
			if (wmModType == null) return null;

			FieldInfo[] fields = wmModType.GetFields(BindingFlags.Public | BindingFlags.Instance);
			foreach (var f in fields)
			{
				if (f.Name == "underAttack")
				{
					bdWmUnderAttackFieldGetter = ReflectionUtils.CreateGetter<object, bool>(f);
					if (CamTools.DEBUG) Debug.Log("[CameraTools.ModIntegration.BDArmory]: Created bdWmUnderAttackFieldGetter.");
					return f;
				}
			}

			return null;
		}

		FieldInfo GetAITargetField()
		{
			if (aiModType == null) return null;

			FieldInfo[] fields = aiModType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
			foreach (var f in fields)
			{
				if (f.Name == "targetVessel")
				{
					bdAITargetFieldGetter = ReflectionUtils.CreateGetter<object, Vessel>(f);
					if (CamTools.DEBUG) Debug.Log("[CameraTools.ModIntegration.BDArmory]: Created bdAITargetFieldGetter.");
					return f;
				}
			}

			return null;
		}

		PropertyInfo GetRecentlyFiringProperty()
		{
			if (wmModType == null) return null;

			PropertyInfo[] propertyInfos = wmModType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
			foreach (var p in propertyInfos)
			{
				if (p != null && p.Name == "recentlyFiring")
				{
					bdWmRecentlyFiringPropertyGetter = ReflectionUtils.BuildGetAccessor(p.GetGetMethod());
					if (CamTools.DEBUG) Debug.Log("[CameraTools.ModIntegration.BDArmory]: Created bdWmRecentlyFiringPropertyGetter.");
					return p;
				}
			}
			return null;
		}
		public void GetBDVessels()
		{
			if (!hasBDA || bdLoadedVesselSwitcherVesselsPropertyGetter == null || bdLoadedVesselSwitcherInstance == null) return;
			bdActiveVessels = (Dictionary<string, List<Vessel>>)bdLoadedVesselSwitcherVesselsPropertyGetter(bdLoadedVesselSwitcherInstance);
			_bdWMVessels = bdActiveVessels.SelectMany(kvp => kvp.Value).ToList(); // FIXME Remove this once SI updates the Centroid mode using bdActiveVessels.
			_bdWMVesselsLastUpdate = Time.time;
		}

		public Vector3 GetCentroid()
		{
			var activeVesselCoM = FlightGlobals.ActiveVessel.CoM;
			Vector3 centroid = activeVesselCoM;
			int count = 1;

			foreach (var v in bdWMVessels)
			{
				if (v == null || !v.loaded || v.packed || v.isActiveVessel) continue;
				if ((v.CoM - activeVesselCoM).magnitude > 20000) continue;
				centroid += v.transform.position;
				++count;
			}
			centroid /= (float)count;
			return centroid;
		}

		public void ToggleInputFields(bool textInput)
		{
			if (textInput)
			{
				foreach (var field in inputFields.Keys)
				{
					try
					{
						var fieldInfo = typeof(BDArmory).GetField(field);
						if (fieldInfo != null) { inputFields[field].currentValue = (float)fieldInfo.GetValue(this); }
						else
						{
							var propInfo = typeof(BDArmory).GetProperty(field);
							inputFields[field].currentValue = (float)propInfo.GetValue(this);
						}
					}
					catch (Exception e)
					{
						Debug.LogError($"[CameraTools.BDArmory]: Failed to get field/property {field}: {e.Message}");
					}
				}
			}
			else
			{
				foreach (var field in inputFields.Keys)
				{
					try
					{
						inputFields[field].tryParseValueNow();
						var fieldInfo = typeof(BDArmory).GetField(field);
						if (fieldInfo != null) { fieldInfo.SetValue(this, inputFields[field].currentValue); }
						else
						{
							var propInfo = typeof(BDArmory).GetProperty(field);
							propInfo.SetValue(this, inputFields[field].currentValue);
						}
					}
					catch (Exception e)
					{
						Debug.LogError($"[CameraTools.BDArmory]: Failed to set field/property {field}: {e.Message}");
					}
				}
			}
		}
	}
}