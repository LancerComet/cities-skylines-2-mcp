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

        // 仅沿被查询雇主的原生Employee缓冲读取本页；不全城找人，不生成通勤或修改员工状态。
        private object DescribeWorkplaceEmployees(Entity employer, int offset, int limit)
        {
            if (!EntityManager.HasBuffer<Game.Companies.Employee>(employer))
                return new { available = false, count = (int?)null, reason = "native_employee_buffer_absent" };

            Entity workplaceBuilding = Entity.Null;
            if (EntityManager.HasComponent<Game.Buildings.Building>(employer)) workplaceBuilding = employer;
            else if (EntityManager.HasComponent<Game.Buildings.PropertyRenter>(employer))
                workplaceBuilding = EntityManager.GetComponentData<Game.Buildings.PropertyRenter>(employer).m_Property;
            bool buildingKnown = workplaceBuilding != Entity.Null && EntityManager.Exists(workplaceBuilding)
                && EntityManager.HasComponent<Game.Buildings.Building>(workplaceBuilding);
            var employees = EntityManager.GetBuffer<Game.Companies.Employee>(employer, isReadOnly: true);
            var rows = new List<object>();
            for (int i = offset; i < employees.Length && rows.Count < limit; i++)
            {
                Entity worker = employees[i].m_Worker;
                bool exists = worker != Entity.Null && EntityManager.Exists(worker);
                bool citizen = exists && EntityManager.HasComponent<Citizen>(worker);
                bool live = citizen && !EntityManager.HasComponent<Game.Common.Deleted>(worker)
                    && !EntityManager.HasComponent<Game.Tools.Temp>(worker);
                bool? matches = live && EntityManager.HasComponent<Worker>(worker)
                    ? (bool?)(EntityManager.GetComponentData<Worker>(worker).m_Workplace == employer) : null;
                bool? atBuilding = live && buildingKnown && EntityManager.HasComponent<CurrentBuilding>(worker)
                    ? (bool?)(EntityManager.GetComponentData<CurrentBuilding>(worker).m_CurrentBuilding == workplaceBuilding) : null;
                rows.Add(new {
                    employeeIndex = i, citizen = Ref(worker), exists, liveCitizen = live,
                    workplaceMatchesEmployer = matches, atEmployerBuilding = atBuilding,
                    citizenState = live ? DescribeCitizenState(worker) : null,
                    travelPurpose = live ? ModEntityComponents(worker, "Game.Citizens.TravelPurpose") : null,
                    currentTransport = live ? ModEntityComponents(worker, "Game.Citizens.CurrentTransport") : null,
                    tripsNeeded = live ? ModEntityComponents(worker, "Game.Citizens.TripNeeded") : null,
                });
            }
            return new {
                available = true, count = employees.Length,
                workplaceBuilding = buildingKnown ? Ref(workplaceBuilding) : null,
                workplaceBuildingKnown = buildingKnown, offset, returned = rows.Count,
                complete = offset == 0 && rows.Count == employees.Length,
                nextOffset = offset + rows.Count < employees.Length ? (object)(offset + rows.Count) : null,
                employees = rows,
                note = "原生只读Employee/Worker/CurrentBuilding及本页市民关联；缺组件为未知，不补0。到岗瞬时位置、原始通勤计数和待出行不证明完成具体步行/公交路线。行程缓冲各自最多128项并注明完整性。",
            };
        }
    }
}
