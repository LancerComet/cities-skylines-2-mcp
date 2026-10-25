using Game.Areas;
using Game.Buildings;
using Game.Citizens;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class BridgeSystem
    {
        // 每请求同步依赖并重取；不保留跨帧/跨加载lookup或Native容器。
        internal HousingReadLookups GetHousingReadLookups()
        {
            EntityManager.CompleteDependencyBeforeRO<Citizen>();
            EntityManager.CompleteDependencyBeforeRO<Household>();
            EntityManager.CompleteDependencyBeforeRO<HouseholdMember>();
            EntityManager.CompleteDependencyBeforeRO<HouseholdCitizen>();
            EntityManager.CompleteDependencyBeforeRO<HealthProblem>();
            EntityManager.CompleteDependencyBeforeRO<PropertyRenter>();
            EntityManager.CompleteDependencyBeforeRO<Renter>();
            EntityManager.CompleteDependencyBeforeRO<PrefabRef>();
            EntityManager.CompleteDependencyBeforeRO<BuildingData>();
            EntityManager.CompleteDependencyBeforeRO<BuildingPropertyData>();
            EntityManager.CompleteDependencyBeforeRO<SpawnableBuildingData>();
            EntityManager.CompleteDependencyBeforeRO<ZoneData>();
            EntityManager.CompleteDependencyBeforeRO<ZonePropertiesData>();
            EntityManager.CompleteDependencyBeforeRO<CurrentDistrict>();
            EntityManager.CompleteDependencyBeforeRO<Locked>();
            EntityManager.CompleteDependencyBeforeRO<HappinessFactorParameterData>();
            return new HousingReadLookups {
                citizens = GetComponentLookup<Citizen>(true), households = GetComponentLookup<Household>(true),
                householdMembers = GetComponentLookup<HouseholdMember>(true), healthProblems = GetComponentLookup<HealthProblem>(true),
                properties = GetComponentLookup<PropertyRenter>(true), prefabs = GetComponentLookup<PrefabRef>(true),
                buildingData = GetComponentLookup<BuildingData>(true), buildingProperties = GetComponentLookup<BuildingPropertyData>(true),
                spawnableBuildings = GetComponentLookup<SpawnableBuildingData>(true), zoneData = GetComponentLookup<ZoneData>(true),
                zoneProperties = GetComponentLookup<ZonePropertiesData>(true), districts = GetComponentLookup<CurrentDistrict>(true),
                locked = GetComponentLookup<Locked>(true), renters = GetBufferLookup<Renter>(true),
                householdCitizens = GetBufferLookup<HouseholdCitizen>(true)
            };
        }

        internal struct HousingReadLookups
        {
            internal ComponentLookup<Citizen> citizens;
            internal ComponentLookup<Household> households;
            internal ComponentLookup<HouseholdMember> householdMembers;
            internal ComponentLookup<HealthProblem> healthProblems;
            internal ComponentLookup<PropertyRenter> properties;
            internal ComponentLookup<PrefabRef> prefabs;
            internal ComponentLookup<BuildingData> buildingData;
            internal ComponentLookup<BuildingPropertyData> buildingProperties;
            internal ComponentLookup<SpawnableBuildingData> spawnableBuildings;
            internal ComponentLookup<ZoneData> zoneData;
            internal ComponentLookup<ZonePropertiesData> zoneProperties;
            internal ComponentLookup<CurrentDistrict> districts;
            internal ComponentLookup<Locked> locked;
            internal BufferLookup<Renter> renters;
            internal BufferLookup<HouseholdCitizen> householdCitizens;
        }
    }
}
