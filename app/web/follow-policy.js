// 自動換角色的優先順序由兩端共用；外殼只提供當前前臺程式對應的角色。
;(function (root) {
  function eventTarget({ eventId, foregroundId, followApps, followEvents, characters }) {
    if (!followEvents || !characters.includes(eventId)) return null
    // 同時開啟時讓前臺 AI 優先，避免後臺每條事件把前臺剛選好的角色搶走。
    // 沒有對應角色的宿主、終端和普通程式仍由工作事件選擇角色。
    if (followApps && characters.includes(foregroundId)) return null
    return eventId
  }
  root.FollowPolicy = { eventTarget }
  if (typeof module !== 'undefined') module.exports = { eventTarget }
})(typeof window !== 'undefined' ? window : globalThis)
