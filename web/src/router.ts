import { createRouter, createWebHashHistory, START_LOCATION } from "vue-router";

import HomeView from "./views/HomeView.vue";
import GuideHostView from "./views/GuideHostView.vue";
import GuideClientView from "./views/GuideClientView.vue";
import WizardView from "./views/WizardView.vue";
const router = createRouter({
  history: createWebHashHistory(import.meta.env.BASE_URL),

  routes: [
    {
      path: "/",
      name: "home",
      component: HomeView,
    },
    {
      path: "/builder",
      name: "builder",
      component: WizardView,
    },
    {
      path: "/setup/host",
      name: "setup-host",
      component: GuideHostView,
    },
    {
      path: "/setup/client",
      name: "setup-client",
      component: GuideClientView,
    },
  ],
});

// GoatCounter (loaded in index.html) counts the first page on load. Hash-history
// navigation uses pushState, which fires no hashchange, so count later routes here.
router.afterEach((_to, from) => {
  if (from === START_LOCATION) return;
  window.goatcounter?.count?.();
});

export default router;
