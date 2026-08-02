<script setup lang="ts">
import { reactive } from "vue";

const items = reactive([
  {
    title: "What is the Slay the Spire 2 Archipelago Mod?",
    description: "It's something",
    icon: "i-simple-icons-discord",
  },
  {
    title: "Installation",
    description: "This is a test message",
    icon: "i-simple-icons-github",
  },
]);
</script>

<template>
  <UPageBody>
    <!-- Logo -->
    <div class="flex flex-col items-center justify-center gap-4">
      <img src="/logo.webp" alt="Slay the Spire 2 Archipelago" class="h-64" />
    </div>

    <!-- Dashboard Cards -->
    <div class="flex flex-col w-full items-center justify-center gap-8">
      <UCard :title="item.title" v-for="item in items" :key="item.title">
        <Placeholder class="h-32" />
      </UCard>
    </div>
  </UPageBody>
</template>
