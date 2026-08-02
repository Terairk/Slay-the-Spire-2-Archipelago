<script setup lang="ts">
import { computed } from "vue";
import { useRoute } from "vue-router";
import type { NavigationMenuItem } from "@nuxt/ui";

const route = useRoute();

const items = computed<NavigationMenuItem[]>(() => [
  {
    label: "Home",
    to: "/",
    active: route.path === "/",
  },
  {
    label: "YAML Builder",
    to: "/builder",
    active: route.path.startsWith("/builder"),
  },
  {
    label: "Help",
    to: "/help",
    active: route.path.startsWith("/help"),
  },
]);
</script>

<template>
  <UHeader>
    <template #title>
      <img src="/logo.webp" alt="Slay the Spire 2 Archipelago" class="h-14" />
    </template>

    <UNavigationMenu :items="items" color="primary" />

    <template #right>
      <UTooltip text="Join our Discord Thread">
        <UButton
          color="neutral"
          variant="ghost"
          to="https://discord.com/channels/731205301247803413/1524232611654799540/1524238637980713062"
          target="_blank"
          icon="i-simple-icons-discord"
          aria-label="Discord"
        />
      </UTooltip>

      <UTooltip text="Visit our GitHub">
        <UButton
          color="neutral"
          variant="ghost"
          to="https://github.com/dlueben1/Slay-the-Spire-2-Archipelago"
          target="_blank"
          icon="i-simple-icons-github"
          aria-label="GitHub"
        />
      </UTooltip>

      <UTooltip text="Get it from Steam Workshop">
        <UButton
          color="neutral"
          variant="ghost"
          to="https://discord.com/channels/731205301247803413/1524232611654799540"
          target="_blank"
          icon="i-simple-icons-steam"
          aria-label="Steam Workshop"
        />
      </UTooltip>
    </template>
  </UHeader>
</template>
