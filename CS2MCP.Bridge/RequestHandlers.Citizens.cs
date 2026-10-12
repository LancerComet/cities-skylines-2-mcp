using System.Collections.Generic;
using Game.Citizens;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        // 只在异常市民被明确查询时读取关联，不扫描全城，也不修改资格或服务请求。
        private object DescribeCitizenState(Entity entity)
        {
            var citizen = EntityManager.GetComponentData<Citizen>(entity);
            var row = new Dictionary<string, object> {
                ["educationLevel"] = citizen.GetEducationLevel(), ["health"] = citizen.m_Health,
                ["wellbeing"] = citizen.m_WellBeing, ["flags"] = citizen.m_State.ToString()
            };
            if (EntityManager.HasComponent<CurrentBuilding>(entity)) {
                Entity building = EntityManager.GetComponentData<CurrentBuilding>(entity).m_CurrentBuilding;
                row["currentBuilding"] = new { entity = Ref(building), prefab = PrefabNameOf(building) };
            }
            if (EntityManager.HasComponent<HouseholdMember>(entity)) {
                Entity household = EntityManager.GetComponentData<HouseholdMember>(entity).m_Household;
                row["household"] = Ref(household);
                if (EntityManager.Exists(household) && EntityManager.HasComponent<Game.Buildings.PropertyRenter>(household)) {
                    Entity home = EntityManager.GetComponentData<Game.Buildings.PropertyRenter>(household).m_Property;
                    row["home"] = new { entity = Ref(home), prefab = PrefabNameOf(home) };
                }
            }
            if (EntityManager.HasComponent<HealthProblem>(entity)) {
                var problem = EntityManager.GetComponentData<HealthProblem>(entity);
                row["healthProblem"] = new { flags = problem.m_Flags.ToString(), timerRaw = problem.m_Timer,
                    eventEntity = Ref(problem.m_Event), request = Ref(problem.m_HealthcareRequest),
                    requestComponents = EntityManager.Exists(problem.m_HealthcareRequest)
                        ? ModEntityComponents(problem.m_HealthcareRequest, "Game.Simulation.") : null };
            }
            if (EntityManager.HasComponent<Worker>(entity)) {
                var worker = EntityManager.GetComponentData<Worker>(entity);
                row["work"] = new { workplace = Ref(worker.m_Workplace), requiredEducationLevel = worker.m_Level,
                    shift = worker.m_Shift.ToString(), lastCommuteTimeRaw = worker.m_LastCommuteTime };
            }
            if (EntityManager.HasComponent<Student>(entity)) {
                var student = EntityManager.GetComponentData<Student>(entity);
                row["study"] = new { school = Ref(student.m_School), schoolLevel = student.m_Level,
                    lastCommuteTimeRaw = student.m_LastCommuteTime };
            }
            row["note"] = "原生只读资格与关联；学历不是岗位要求，timer/commute保持原始单位。请求存在不证明已经到达。";
            return row;
        }
    }
}
